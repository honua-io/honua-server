// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Npgsql;

namespace Honua.Server.Tests.Realtime;

// Actual loopback HTTP connections to two independent application hosts on one database.
// This covers the feature HTTP path in specification section 12; it is not the separate
// process-replacement, all-family, scale or deployed-CDN qualification.
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class TwoHostFeatureHttpTests : IAsyncLifetime
{
    private const int Edits = 200;
    private readonly string _marker = $"twohost-{Guid.NewGuid():N}";
    private readonly WebAppFixture _taskA = TwoHostFeatureHarness.NewHost();
    private WebAppFixture _taskB = null!;
    private bool _taskBStopped;

    public async Task InitializeAsync()
    {
        await _taskA.InitializeAsync();
        TwoHostFeatureHarness.EnableEditing(_taskA);
        _taskB = TwoHostFeatureHarness.NewHost().ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
        TwoHostFeatureHarness.EnableEditing(_taskB);

        _taskA.Client.BaseAddress.Should().NotBe(_taskB.Client.BaseAddress);
        _taskA.CurrentSchema.Should().Be(_taskB.CurrentSchema);
        var providerA = _taskA.GetService<IRealtimeFanoutProvider>();
        var providerB = _taskB.GetService<IRealtimeFanoutProvider>();
        providerA.Should().NotBeSameAs(providerB);
        providerA.Capabilities.CrossNode.Should().BeTrue();
        providerB.Capabilities.CrossNode.Should().BeTrue();

        // Establish a nonzero head and let both production relays finish their initial
        // live-only positioning before measuring any subscriber delivery.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = _taskA.CreateAdminClient();
        await AddAsync(client, -1, timeout.Token);
        await TwoHostFeatureHarness.WaitForRelayAsync(_taskA, timeout.Token);
        await TwoHostFeatureHarness.WaitForRelayAsync(_taskB, timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (_taskB is not null && !_taskBStopped)
        {
            await _taskB.DisposeAsync();
        }
        await _taskA.DisposeAsync();
    }

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task HttpEditsOnTaskA_ReachTaskBSse_OnceInOrderWithinTarget()
        => await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(500));

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task DirectSqlEdits_ReachTaskBSse_OnceInOrderWithinTarget()
        => await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(500),
            edit: (_, ordinal, cancellationToken) => AddSqlAsync(ordinal, cancellationToken));

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task DirectSqlEdits_WithoutTaskBListener_ReachSseWithinDegradedPollTarget()
    {
        await _taskB.DisposeAsync();
        _taskBStopped = true;
        _taskB = TwoHostFeatureHarness.NewHost(new Dictionary<string, string?>
            {
                // A transaction proxy deliberately selects the production poll-only path;
                // the ordinary database connection still targets the shared fixture database.
                ["ConnectionStrings:RealtimeListener"] = "Host=disabled.proxy-local;Database=unused;Username=unused"
            }).ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
        _taskBStopped = false;
        TwoHostFeatureHarness.EnableEditing(_taskB);
        var listener = _taskB.GetService<RealtimeListener>();
        listener.PollOnlyReason.Should().Be("the database host is a connection proxy");
        listener.IsConnected.Should().BeFalse();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await TwoHostFeatureHarness.WaitForRelayAsync(_taskB, timeout.Token);

        await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(1500),
            edit: (_, ordinal, cancellationToken) => AddSqlAsync(ordinal, cancellationToken));
    }

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task ListenerKilledDuringHttpEdits_TaskBSseLosesNothingAndListenerReconnects()
    {
        var listener = _taskB.GetService<RealtimeListener>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!listener.IsConnected)
        {
            await Task.Delay(20, timeout.Token);
        }
        var reconnects = listener.Reconnects;
        var processId = listener.ProcessId;
        processId.Should().BeGreaterThan(0);

        await VerifyDeliveryAsync(TimeSpan.FromMilliseconds(1500), async (ordinal, cancellationToken) =>
        {
            if (ordinal != Edits / 2)
            {
                return;
            }
            await using var connection = new NpgsqlConnection(_taskB.DatabaseConnectionProvider.GetConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", connection);
            command.Parameters.AddWithValue("pid", processId);
            (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(true);
        });

        while (!listener.IsConnected || listener.Reconnects <= reconnects)
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    private async Task VerifyDeliveryAsync(
        TimeSpan latencyTarget, Func<int, CancellationToken, Task>? beforeEdit = null,
        Func<HttpClient, int, CancellationToken, Task<long>>? edit = null)
    {
        edit ??= AddAsync;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var writer = _taskA.CreateAdminClient();
        using var subscriber = _taskB.CreateAdminClient();
        using var response = await TwoHostFeatureHarness.OpenStreamAsync(subscriber, null, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var reading = ReadChangesAsync(reader, Edits, timeout.Token);
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var relayTimes = SampleRelayAsync(TwoHostFeatureHarness.Relay(_taskB), sampling.Token);
        var committed = new Dictionary<long, long>();
        for (var i = 0; i < Edits; i++)
        {
            if (beforeEdit is not null)
            {
                await beforeEdit(i, timeout.Token);
            }
            var startedAt = Stopwatch.GetTimestamp();
            committed.Add(await edit(writer, i, timeout.Token), startedAt);
        }

        var frames = await reading;
        await sampling.CancelAsync();
        var relayed = await relayTimes;
        AssertCompleteOrderedDelivery(frames, committed.Keys);
        // Measure from before the HTTP edit, so this includes request and commit time
        // rather than understating latency when SSE beats the edit response back.
        var latencies = frames.Select(frame => Stopwatch.GetElapsedTime(committed[frame.ObjectId], frame.ReceivedAt))
            .Order().ToArray();
        // Split each delivery at the moment task B's relay passed the frame's generation, so a
        // miss says whether the wait was in the provider (commit to relay) or the stream (relay to frame).
        var toRelay = frames.Select(frame => Stopwatch.GetElapsedTime(committed[frame.ObjectId], RelayedAt(relayed, frame)))
            .Order().ToArray();
        var toFrame = frames.Select(frame => Stopwatch.GetElapsedTime(RelayedAt(relayed, frame), frame.ReceivedAt))
            .Order().ToArray();
        var p95 = (int)Math.Ceiling(Edits * 0.95) - 1;
        var listenerB = _taskB.GetService<RealtimeListener>();
        var logHeadB = await _taskB.GetService<PostgresFeatureChangeLog>().ReadHeadAsync(timeout.Token);
        latencies[p95].Should().BeLessThanOrEqualTo(latencyTarget,
            $"p95 commit-to-relay was {toRelay[p95].TotalMilliseconds:F0} ms and relay-to-frame {toFrame[p95].TotalMilliseconds:F0} ms; " +
            $"relay moved {relayed.Count} times ({DescribeRelay(relayed)}) for frame generations " +
            $"{frames[0].Generation}..{frames[^1].Generation}; task B feature log head {logHeadB}, " +
            $"listener connected {listenerB.IsConnected} (pid {listenerB.ProcessId}, reconnects {listenerB.Reconnects}, " +
            $"poll-only reason {listenerB.PollOnlyReason ?? "none"})");
    }

    // Records when task B's feature relay first reached each position, sampled every few
    // milliseconds; the relay position is the generation it last handed to local sessions.
    private static async Task<List<(long Position, long At)>> SampleRelayAsync(
        FeatureStreamRealtimeRelay relay, CancellationToken cancellationToken)
    {
        var samples = new List<(long Position, long At)>();
        var last = relay.Position;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var position = relay.Position;
                if (position != last)
                {
                    samples.Add((position, Stopwatch.GetTimestamp()));
                    last = position;
                }
                await Task.Delay(2, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Sampling ends with delivery.
        }
        return samples;
    }

    private static string DescribeRelay(List<(long Position, long At)> samples)
        => samples.Count == 0
            ? "never"
            : $"first {samples[0].Position}, last {samples[^1].Position}, " +
              $"largest step {samples.Zip(samples.Skip(1), (a, b) => b.Position - a.Position).DefaultIfEmpty(samples[0].Position).Max()}";

    private static long RelayedAt(List<(long Position, long At)> samples, FeatureFrame frame)
        => samples.FirstOrDefault(sample => sample.Position >= frame.Generation) is { At: > 0 } sample
            ? sample.At
            : frame.ReceivedAt;

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task AfterTaskBStops_LastEventIdOnTaskA_ReplaysEveryRemainingHttpEdit()
    {
        const int beforeStop = 50;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var writer = _taskA.CreateAdminClient();
        var committed = new List<long>();
        IReadOnlyList<FeatureFrame> before;
        using (var subscriber = _taskB.CreateAdminClient())
        using (var response = await TwoHostFeatureHarness.OpenStreamAsync(subscriber, null, timeout.Token))
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token)))
        {
            var reading = ReadChangesAsync(reader, beforeStop, timeout.Token);
            for (var i = 0; i < beforeStop; i++)
            {
                committed.Add(await AddAsync(writer, i, timeout.Token));
            }
            before = await reading;
        }

        await _taskB.DisposeAsync();
        _taskBStopped = true;
        for (var i = beforeStop; i < Edits; i++)
        {
            committed.Add(await AddAsync(writer, i, timeout.Token));
        }

        using var resumed = _taskA.CreateAdminClient();
        using var replay = await TwoHostFeatureHarness.OpenStreamAsync(resumed, before[^1].Id, timeout.Token);
        using var replayReader = new StreamReader(await replay.Content.ReadAsStreamAsync(timeout.Token));
        var after = await ReadChangesAsync(replayReader, Edits - beforeStop, timeout.Token);
        AssertCompleteOrderedDelivery(before.Concat(after).ToArray(), committed);
    }

    [IntegrationTest(Timeout = 240000)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task TaskBStopsMidStream_ClientResumesOnTaskA_WithNoLossOrDuplicate()
    {
        const int beforeStop = 20;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var writer = _taskA.CreateAdminClient();
        var committed = new List<long>();
        using var subscriber = _taskB.CreateAdminClient();
        using var response = await TwoHostFeatureHarness.OpenStreamAsync(subscriber, null, timeout.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(timeout.Token));
        var reading = ReadUntilStreamClosesAsync(reader, timeout.Token);
        for (var i = 0; i < Edits; i++)
        {
            if (i == beforeStop)
            {
                // Task B shuts down underneath the open stream; the rest of the edits commit on
                // task A only after it has gone, so they can reach the client only by resuming.
                _taskBStopped = true;
                await _taskB.DisposeAsync();
            }
            committed.Add(await AddAsync(writer, i, timeout.Token));
        }
        var before = await reading;
        before.Should().NotBeEmpty("task B served the stream before it stopped");
        before.Count.Should().BeLessThanOrEqualTo(beforeStop, "task B stopped before the later edits committed");

        using var resumed = _taskA.CreateAdminClient();
        using var replay = await TwoHostFeatureHarness.OpenStreamAsync(resumed, before[^1].Id, timeout.Token);
        using var replayReader = new StreamReader(await replay.Content.ReadAsStreamAsync(timeout.Token));
        var after = await ReadChangesAsync(replayReader, Edits - before.Count, timeout.Token);
        AssertCompleteOrderedDelivery(before.Concat(after).ToArray(), committed);
    }

    private Task<long> AddAsync(HttpClient client, int ordinal, CancellationToken cancellationToken)
        => TwoHostFeatureHarness.AddAsync(
            client, ordinal < 0 ? $"warmup-{_marker}" : $"{_marker}-{ordinal}", cancellationToken);

    // The shared fixture helper writes SQL in the isolated schema, with no HTTP request
    // or local publish. The database trigger is the only source of this event.
    private Task<long> AddSqlAsync(int ordinal, CancellationToken cancellationToken)
        => _taskA.InsertFeatureAsync(0, $"{_marker}-{ordinal}", cancellationToken);

    private async Task<IReadOnlyList<FeatureFrame>> ReadChangesAsync(
        StreamReader reader, int count, CancellationToken cancellationToken)
    {
        var frames = new List<FeatureFrame>(count);
        var stream = new MarkedFeatureStream(_marker);
        try
        {
            await foreach (var frame in stream.ReadAsync(reader, cancellationToken))
            {
                frames.Add(frame);
                if (frames.Count == count)
                {
                    return frames;
                }
            }
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException($"Received {frames.Count} of {count} marked edits; {stream.Describe()}", exception);
        }
        throw new EndOfStreamException(
            $"The feature SSE stream ended after {frames.Count} of {count} marked edits; {stream.Describe()}");
    }

    // Reads until the serving task goes away. The client only knows what it received, so
    // whatever arrived before the connection broke is the resume point.
    private async Task<IReadOnlyList<FeatureFrame>> ReadUntilStreamClosesAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        var frames = new List<FeatureFrame>();
        try
        {
            await foreach (var frame in new MarkedFeatureStream(_marker).ReadAsync(reader, cancellationToken))
            {
                frames.Add(frame);
            }
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // An aborted response surfaces as a transport error rather than a clean end.
        }
        return frames;
    }

    private static void AssertCompleteOrderedDelivery(IReadOnlyList<FeatureFrame> frames, IEnumerable<long> committed)
    {
        frames.Select(frame => frame.ObjectId).Should().Equal(committed, "every edit must arrive once in commit order");
        frames.Select(frame => frame.Generation).Should().OnlyHaveUniqueItems().And.BeInAscendingOrder();
    }

}
