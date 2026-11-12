// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Server.Features.Realtime;
using Honua.Server.Features.Streaming;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Realtime;

// Specification realtime-cross-node-rc3 section 12, tests 1 and 3, for feature streams. Each task
// is what a serving process runs: its listener, its tail, its dispatcher, its relay and its stream
// sessions, over the one shared database. Edits are plain SQL, so no task knows about them except
// through the change log.
public sealed class TwoTaskFeatureStreamTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const int Edits = 200;
    private static readonly TimeSpan _deliveryTimeout = TimeSpan.FromSeconds(30);

    [IntegrationTest(Timeout = 240000)]
    public async Task DirectSqlEdits_ReachASubscriberOnAnotherTask_OnceInOrderWithinTarget()
    {
        var latencies = await MeasureCrossTaskDeliveryAsync(734210, listen: true);

        Percentile(latencies, 0.95).Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(500));
    }

    [IntegrationTest(Timeout = 240000)]
    public async Task DirectSqlEdits_WithoutAListener_ReachAnotherTaskWithinTheDegradedPoll()
    {
        var latencies = await MeasureCrossTaskDeliveryAsync(734211, listen: false);

        latencies.Max().Should().BeLessThanOrEqualTo(TimeSpan.FromMilliseconds(1_500));
    }

    [IntegrationTest(Timeout = 240000)]
    public Task Resume_AfterGracefulTaskStop_OnAnotherTask_LosesNothing()
        => ResumeAfterTaskStopAsync(734212, graceful: true);

    [IntegrationTest(Timeout = 240000)]
    public Task Resume_AfterAbruptTaskStop_OnAnotherTask_LosesNothing()
        => ResumeAfterTaskStopAsync(734213, graceful: false);

    private async Task<IReadOnlyList<TimeSpan>> MeasureCrossTaskDeliveryAsync(int layerId, bool listen)
    {
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, layerId);
        await layer.InsertAsync("""{"warm-up":true}""");
        await using var taskA = await FeatureTask.StartAsync(database, listen);
        await using var taskB = await FeatureTask.StartAsync(database, listen);
        using var subscriber = taskB.Sessions.CreateSession("sse", "two-task");

        var committed = new Dictionary<long, long>();
        var reading = ReadAsync(subscriber, layerId, Edits);
        for (var i = 0; i < Edits; i++)
        {
            var objectId = await layer.InsertAsync("""{"n":1}""");
            committed[objectId] = Stopwatch.GetTimestamp();
        }

        var received = await reading;
        var generations = (await layer.GenerationsAsync()).Skip(1);
        received.Select(frame => frame.Generation).Should().Equal(generations, "every edit arrives once, in generation order");
        return received.Select(frame => Stopwatch.GetElapsedTime(committed[frame.ObjectId], frame.ReceivedAt)).ToArray();
    }

    // A subscriber reads part of the stream from task A; A stops while edits continue; the client
    // reconnects to task B with its last id, as an SSE stream does: B's live session first, then a
    // replay after that id, then the live frames the replay did not cover.
    private async Task ResumeAfterTaskStopAsync(int layerId, bool graceful)
    {
        const int beforeStop = 50;
        await using var layer = await FeatureChangeTestLayer.CreateAsync(database, layerId);
        await layer.InsertAsync("""{"warm-up":true}""");
        var taskA = await FeatureTask.StartAsync(database, listen: true);
        await using var taskB = await FeatureTask.StartAsync(database, listen: true);

        IReadOnlyList<Frame> fromA;
        using (var subscriberOnA = taskA.Sessions.CreateSession("sse", "before-stop"))
        {
            var reading = ReadAsync(subscriberOnA, layerId, beforeStop);
            for (var i = 0; i < beforeStop; i++)
            {
                await layer.InsertAsync("""{"n":1}""");
            }

            fromA = await reading;
        }

        await taskA.StopAsync(graceful);
        using var subscriberOnB = taskB.Sessions.CreateSession("sse", "after-stop");
        var writing = Task.Run(async () =>
        {
            for (var i = beforeStop; i < Edits; i++)
            {
                await layer.InsertAsync("""{"n":1}""");
            }
        });

        var lastId = fromA[^1].Generation;
        var replayed = await ReplayAsync(taskB.Store, layerId, lastId);
        await writing;
        var expected = (await layer.GenerationsAsync()).Skip(1).ToArray();
        var live = await ReadThroughAsync(subscriberOnB, layerId, expected[^1]);

        var union = fromA.Select(frame => frame.Generation)
            .Concat(replayed)
            .Concat(live.Select(frame => frame.Generation))
            .Distinct()
            .ToArray();
        union.Should().Equal(expected, "the union of what the client received, deduplicated by id, is the committed set in order");
    }

    private static async Task<IReadOnlyList<long>> ReplayAsync(PostgresFeatureChangeEventStore store, int layerId, long after)
    {
        var replayed = new List<long>();
        var cursor = after;
        while (true)
        {
            var page = await store.QueryAsync(cursor, null, null, 50);
            replayed.AddRange(page.Where(change => change.LayerId == layerId).Select(change => change.Cursor));
            if (page.Count < 50)
            {
                return replayed;
            }

            cursor = page[^1].Cursor;
        }
    }

    private static async Task<IReadOnlyList<Frame>> ReadAsync(FeatureStreamSession session, int layerId, int count)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var frames = new List<Frame>();
        while (frames.Count < count)
        {
            var message = await session.Reader.ReadAsync(timeout.Token);
            if (!message.IsHeartbeat && message.Envelope.LayerId == layerId)
            {
                frames.Add(new Frame(message.Envelope.Cursor, message.Envelope.ObjectId, Stopwatch.GetTimestamp()));
            }
        }

        return frames;
    }

    private static async Task<IReadOnlyList<Frame>> ReadThroughAsync(FeatureStreamSession session, int layerId, long lastGeneration)
    {
        using var timeout = new CancellationTokenSource(_deliveryTimeout);
        var frames = new List<Frame>();
        while (frames.Count == 0 || frames[^1].Generation < lastGeneration)
        {
            var message = await session.Reader.ReadAsync(timeout.Token);
            if (!message.IsHeartbeat && message.Envelope.LayerId == layerId)
            {
                frames.Add(new Frame(message.Envelope.Cursor, message.Envelope.ObjectId, Stopwatch.GetTimestamp()));
            }
        }

        return frames;
    }

    private static TimeSpan Percentile(IReadOnlyList<TimeSpan> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        return sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1];
    }

    private readonly record struct Frame(long Generation, long ObjectId, long ReceivedAt);

    // One serving task over the shared database.
    private sealed class FeatureTask : IAsyncDisposable
    {
        private readonly RealtimeTailBackgroundService _tail;
        private readonly FeatureStreamRealtimeRelay _relay;
        private bool _stopped;

        private FeatureTask(RealtimeTailBackgroundService tail, FeatureStreamRealtimeRelay relay, FeatureStreamSessionManager sessions, PostgresFeatureChangeEventStore store)
        {
            _tail = tail;
            _relay = relay;
            Sessions = sessions;
            Store = store;
        }

        public FeatureStreamSessionManager Sessions { get; }

        public PostgresFeatureChangeEventStore Store { get; }

        public static async Task<FeatureTask> StartAsync(DatabaseFixtureAdapter database, bool listen)
        {
            var options = Options.Create(new RealtimeOptions());
            var jobs = new JobStoreDatabase(database.DataSource);
            var log = new PostgresRealtimeEventLog(jobs);
            var changes = new PostgresFeatureChangeLog(jobs, options, TimeProvider.System);
            var dispatcher = new RealtimeDispatcher(log, [changes], options, NullLogger<RealtimeDispatcher>.Instance);
            var presence = new RealtimePresenceBatcher(log);
            var listener = listen
                ? RealtimeListener.Create(null, database.ConnectionString, options, NullLogger<RealtimeListener>.Instance)
                : null;
            var tail = new RealtimeTailBackgroundService(dispatcher, presence, listener, options, NullLogger<RealtimeTailBackgroundService>.Instance);
            var sessions = new FeatureStreamSessionManager(
                Options.Create(new FeatureStreamOptions()),
                NullLogger<FeatureStreamSessionManager>.Instance,
                TestTelemetry.CreateFeatureStreamMetrics());
            var relay = new FeatureStreamRealtimeRelay(
                new PostgresRealtimeFanoutProvider(log, dispatcher, presence),
                sessions,
                NullLogger<FeatureStreamRealtimeRelay>.Instance);
            await tail.StartAsync(CancellationToken.None);
            await relay.StartAsync(CancellationToken.None);
            await WaitUntilPositionedAsync(relay);
            return new FeatureTask(tail, relay, sessions, new PostgresFeatureChangeEventStore(changes));
        }

        // Graceful stops the services; abrupt abandons them, as a killed process does.
        public async Task StopAsync(bool graceful)
        {
            _stopped = true;
            if (graceful)
            {
                await _relay.StopAsync(CancellationToken.None);
                await _tail.StopAsync(CancellationToken.None);
            }

            _relay.Dispose();
            _tail.Dispose();
            Sessions.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                await StopAsync(graceful: true);
            }
        }

        // Edits made after this are after the relay's starting head, so none is skipped.
        private static async Task WaitUntilPositionedAsync(FeatureStreamRealtimeRelay relay)
        {
            using var timeout = new CancellationTokenSource(_deliveryTimeout);
            while (relay.Position == 0)
            {
                await Task.Delay(20, timeout.Token);
            }
        }
    }
}
