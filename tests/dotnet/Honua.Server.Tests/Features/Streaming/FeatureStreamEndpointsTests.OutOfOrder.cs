// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.WebSockets;
using FluentAssertions;
using Honua.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Regression coverage for honua-server#6104: live default subscriptions must deliver a frame
/// whose cursor is lower than one already delivered (out-of-order arrival from publisher
/// continuations or cross-node Redis fan-out) while still dropping true duplicates.
/// </summary>
public sealed partial class FeatureStreamEndpointsTests
{
    private static readonly TimeSpan OutOfOrderSettleWindow = TimeSpan.FromMilliseconds(1500);

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task Sse_DefaultSubscription_DeliversLowerCursorArrivingAfterHigherCursorExactlyOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            "/api/v1/streaming/features?clientLabel=sse-out-of-order");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token);
        using var reader = new StreamReader(stream);
        var status = await ReadNextSseEventAsync(reader, cts.Token);
        status.EventName.Should().Be("status");
        await WaitForSessionAsync(sessionManager, "sse-out-of-order", cts.Token);

        var (higher, lower) = await BroadcastOutOfOrderPairWithDuplicateAsync(sessionManager, eventStore, cts.Token);

        var delivered = new List<string>();
        using var settle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        settle.CancelAfter(OutOfOrderSettleWindow);
        try
        {
            while (true)
            {
                var sseEvent = await ReadNextSseEventAsync(reader, settle.Token);
                if (sseEvent.EventName == "feature-change")
                {
                    delivered.Add(sseEvent.Data.GetProperty("eventId").GetString()!);
                }
            }
        }
        catch (OperationCanceledException) when (settle.Token.IsCancellationRequested && !cts.IsCancellationRequested)
        {
            // Settle window elapsed.
        }

        AssertOutOfOrderDelivery(delivered, higher, lower);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task WebSocket_DefaultSubscription_DeliversLowerCursorArrivingAfterHigherCursorExactlyOnce()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sessionManager = _fixture.GetService<FeatureStreamSessionManager>();
        var eventStore = _fixture.GetService<IFeatureChangeEventStore>();

        var wsClient = _fixture.CreateWebSocketClient();
        using var ws = await wsClient.ConnectAsync(
            new Uri("ws://localhost/api/v1/streaming/features?clientLabel=ws-out-of-order"),
            cts.Token);
        _ = await ReceiveWebSocketJsonAsync(ws, cts.Token);
        await WaitForSessionAsync(sessionManager, "ws-out-of-order", cts.Token);

        var (higher, lower) = await BroadcastOutOfOrderPairWithDuplicateAsync(sessionManager, eventStore, cts.Token);

        var delivered = new List<string>();
        using var settle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        settle.CancelAfter(OutOfOrderSettleWindow);
        try
        {
            while (true)
            {
                var frame = await ReceiveWebSocketJsonAsync(ws, settle.Token);
                if (IsFeatureChangeFrame(frame))
                {
                    delivered.Add(frame.GetProperty("eventId").GetString()!);
                }
            }
        }
        catch (OperationCanceledException) when (settle.Token.IsCancellationRequested && !cts.IsCancellationRequested)
        {
            // Settle window elapsed.
        }

        AssertOutOfOrderDelivery(delivered, higher, lower);
    }

    private static async Task<(FeatureStreamEnvelope Higher, FeatureStreamEnvelope Lower)> BroadcastOutOfOrderPairWithDuplicateAsync(
        FeatureStreamSessionManager sessionManager,
        IFeatureChangeEventStore eventStore,
        CancellationToken cancellationToken)
    {
        // Synthetic cursors far above the durable head so no stored event collides with them.
        var head = await eventStore.GetCurrentCursorAsync(cancellationToken);
        var higher = CreateOutOfOrderEnvelope(head + 1_000_106, "higher");
        var lower = CreateOutOfOrderEnvelope(head + 1_000_105, "lower");

        // B (higher cursor) reaches the session first; A (lower cursor) arrives afterwards.
        sessionManager.Broadcast(FeatureStreamMessage.Data(higher));
        sessionManager.Broadcast(FeatureStreamMessage.Data(lower));

        // A true duplicate (same event id and cursor), e.g. a cross-node echo.
        sessionManager.Broadcast(FeatureStreamMessage.Data(higher));
        return (higher, lower);
    }

    private static FeatureStreamEnvelope CreateOutOfOrderEnvelope(long cursor, string label) => new()
    {
        EventId = $"out-of-order-{label}-{Guid.NewGuid():N}",
        Cursor = cursor,
        Timestamp = DateTimeOffset.UtcNow,
        ServiceId = "test",
        LayerId = 0,
        ObjectId = cursor,
        Operation = "insert",
        Protocol = "rest",
        RequestId = $"req-out-of-order-{label}"
    };

    private static void AssertOutOfOrderDelivery(
        IReadOnlyList<string> delivered,
        FeatureStreamEnvelope higher,
        FeatureStreamEnvelope lower)
    {
        delivered.Count(id => id == higher.EventId).Should().Be(
            1,
            "the higher-cursor frame is delivered once and its duplicate is dropped");
        delivered.Count(id => id == lower.EventId).Should().Be(
            1,
            "a lower-cursor frame that arrives after a higher cursor was never delivered and must not be fenced out (#6104)");
    }
}
