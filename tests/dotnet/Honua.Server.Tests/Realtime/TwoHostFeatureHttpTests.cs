// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Realtime;
using Honua.Server.Features.Realtime;
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
        AssertCompleteOrderedDelivery(frames, committed.Keys);
        // Measure from before the HTTP edit, so this includes request and commit time
        // rather than understating latency when SSE beats the edit response back.
        var latencies = frames.Select(frame => Stopwatch.GetElapsedTime(committed[frame.ObjectId], frame.ReceivedAt))
            .Order().ToArray();
        latencies[(int)Math.Ceiling(Edits * 0.95) - 1].Should().BeLessThanOrEqualTo(latencyTarget);
    }

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
        Task? stopping = null;
        for (var i = 0; i < Edits; i++)
        {
            if (i == beforeStop)
            {
                // Edits keep committing on task A while task B shuts down underneath an open
                // stream, so the break lands at whatever point delivery had reached.
                _taskBStopped = true;
                stopping = _taskB.DisposeAsync();
            }
            committed.Add(await AddAsync(writer, i, timeout.Token));
        }
        await stopping!;
        var before = await reading;
        before.Should().NotBeEmpty("task B served the stream before it stopped");
        before.Count.Should().BeLessThan(Edits, "task B stopped before the edits finished");

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
