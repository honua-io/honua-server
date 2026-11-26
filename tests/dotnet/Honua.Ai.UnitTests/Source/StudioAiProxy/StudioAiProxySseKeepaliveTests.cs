// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Server.Features.Studio.Ai;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.StudioAiProxy;

[Protocol(TestProtocols.Admin)]
public sealed class StudioAiProxySseKeepaliveTests
{
    [UnitTest]
    public async Task StreamSseEventsWithKeepaliveAsync_SlowUpstreamStream_EmitsKeepalivesWithoutCorruptingFrames()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;

        var firstTokenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondTokenGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<StudioAiChatEvent> SlowStream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await firstTokenGate.Task.WaitAsync(cancellationToken);
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStart,
                Model = "claude-test"
            };
            await secondTokenGate.Task.WaitAsync(cancellationToken);
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.TextDelta,
                Text = "hello world"
            };
            yield return new StudioAiChatEvent
            {
                Type = StudioAiChatEventType.MessageStop,
                StopReason = StudioAiStopReason.EndTurn
            };
        }

        var streaming = StudioAiProxyEndpoints.StreamSseEventsWithKeepaliveAsync(
            context.Response,
            SlowStream(),
            new StudioAiProxyEndpoints.StudioAiSseStreamOptions(
                TimeSpan.FromMilliseconds(25),
                CancellationToken.None));

        // Wait for at least one keepalive before first token, then release first token.
        await WaitUntilAsync(() => ReadOutput(body).Contains(": keepalive\n\n", StringComparison.Ordinal));
        firstTokenGate.TrySetResult();

        // Wait for message_start to flush and at least one more keepalive during inter-event pause.
        await WaitUntilAsync(() => CountKeepalives(ReadOutput(body)) >= 2
            && ReadOutput(body).Contains("event: message_start\n", StringComparison.Ordinal));
        secondTokenGate.TrySetResult();

        await streaming;

        var wire = ReadOutput(body);
        var frames = wire.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        frames.Should().Contain(": keepalive");

        var dataFrames = frames.Where(static frame => frame != ": keepalive").ToArray();
        dataFrames.Should().HaveCount(3);
        foreach (var frame in dataFrames)
        {
            var lines = frame.Split('\n');
            lines.Should().HaveCount(2, "each SSE data frame must stay intact with no interleaved keepalive bytes");
            lines[0].Should().StartWith("event: ");
            lines[1].Should().StartWith("data: ");
            using var parsed = JsonDocument.Parse(lines[1]["data: ".Length..]);
            parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        }

        dataFrames[0].Should().StartWith("event: message_start\n");
        dataFrames[1].Should().StartWith("event: text_delta\n").And.Contain("\"hello world\"");
        dataFrames[2].Should().StartWith("event: message_stop\n");
    }

    [UnitTest]
    public async Task StreamSseEventsWithKeepaliveAsync_CancelledRequest_StopsKeepaliveAndCancelsUpstream()
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        var upstreamCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<StudioAiChatEvent> BlockedStream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                upstreamCancelled.TrySetResult();
                throw;
            }

            yield break;
        }

        using var cts = new CancellationTokenSource();
        var streaming = StudioAiProxyEndpoints.StreamSseEventsWithKeepaliveAsync(
            context.Response,
            BlockedStream(),
            new StudioAiProxyEndpoints.StudioAiSseStreamOptions(TimeSpan.FromMilliseconds(20), cts.Token));

        await WaitUntilAsync(() => CountKeepalives(ReadOutput(body)) >= 1);
        await cts.CancelAsync();

        var act = () => streaming;
        await act.Should().ThrowAsync<OperationCanceledException>();
        await upstreamCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static string ReadOutput(MemoryStream body) => Encoding.UTF8.GetString(body.ToArray());

    private static int CountKeepalives(string wire) =>
        wire.Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Count(static frame => frame == ": keepalive");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for expected SSE frame.");
            }

            await Task.Delay(10);
        }
    }
}
