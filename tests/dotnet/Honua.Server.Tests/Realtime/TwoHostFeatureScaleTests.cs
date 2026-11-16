// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Honua.Server.Features.Realtime;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Realtime;

// Specification realtime-cross-node-rc3 section 12 item 9: two tasks on one database, subscribers
// split across both, edits written through both, every subscriber receiving every edit once and in
// commit order with p95 delivery within one second and one listener connection per task.
// Opt in with HONUA_REALTIME_SCALE=1. Defaults are the specification profile (2,000
// subscribers, 50 edits per second, 600 seconds); HONUA_REALTIME_SCALE_SUBSCRIBERS,
// HONUA_REALTIME_SCALE_EDITS_PER_SECOND and HONUA_REALTIME_SCALE_SECONDS shorten a
// smoke run. The database CPU criterion compares two runs of this test on the same database host,
// one with HONUA_REALTIME_SCALE_SUBSCRIBERS=0; the database host is measured outside the test.
[Collection("Database")]
[Protocol(TestProtocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class TwoHostFeatureScaleTests : IAsyncLifetime
{
    private static readonly TimeSpan DeliveryTarget = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(60);
    private readonly ScaleProfile _profile = ScaleProfile.FromEnvironment();
    private readonly string _marker = $"scale-{Guid.NewGuid():N}";
    private readonly ITestOutputHelper _output;
    private WebAppFixture _taskA = null!;
    private WebAppFixture _taskB = null!;

    public TwoHostFeatureScaleTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        // Each task holds half of the subscribers; the default session cap of 256 per task is a
        // production setting an operator raises for this load, so the run raises it the same way.
        var perTask = Math.Max(1, (_profile.Subscribers + 1) / 2);
        var overrides = new Dictionary<string, string?>
        {
            ["FeatureStreaming:MaxConcurrentSessions"] = (perTask + 16).ToString(CultureInfo.InvariantCulture)
        };
        _taskA = TwoHostFeatureHarness.NewHost(overrides);
        await _taskA.InitializeAsync();
        TwoHostFeatureHarness.EnableEditing(_taskA);
        _taskB = TwoHostFeatureHarness.NewHost(overrides).ShareSchemaWith(_taskA);
        await _taskB.InitializeAsync();
        TwoHostFeatureHarness.EnableEditing(_taskB);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var client = _taskA.CreateAdminClient();
        await TwoHostFeatureHarness.AddAsync(client, $"warmup-{_marker}", timeout.Token);
        await TwoHostFeatureHarness.WaitForRelayAsync(_taskA, timeout.Token);
        await TwoHostFeatureHarness.WaitForRelayAsync(_taskB, timeout.Token);
    }

    public async Task DisposeAsync()
    {
        if (_taskB is not null)
        {
            await _taskB.DisposeAsync();
        }
        if (_taskA is not null)
        {
            await _taskA.DisposeAsync();
        }
    }

    [ScaleTest("HONUA_REALTIME_SCALE")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /api/v1/streaming/features")]
    public async Task TwoTasks_SubscribersOnBoth_ReceiveEveryEditOnceInOrderWithinTarget()
    {
        var total = _profile.TotalEdits;
        using var run = new CancellationTokenSource(_profile.Duration + SettleTimeout + TimeSpan.FromMinutes(2));
        var listeners = new[] { _taskA.GetService<RealtimeListener>(), _taskB.GetService<RealtimeListener>() };
        var reconnectsBefore = listeners.Select(listener => listener.Reconnects).ToArray();
        var listenerSessionsBefore = await CountListenerSessionsAsync();
        var startedAt = new long[total];
        var latencies = new LatencyHistogram();

        using var clientA = SubscriberClient(_taskA);
        using var clientB = SubscriberClient(_taskB);
        var subscribers = new List<Subscriber>(_profile.Subscribers);
        Task[] reading = [];
        try
        {
            await OpenSubscribersAsync(clientA, clientB, subscribers, run.Token);
            _output.WriteLine($"after opening subscribers: {await DescribeDatabaseSessionsAsync()}");
            var context = new DeliveryContext(_marker, startedAt, latencies);
            reading = subscribers.Select(subscriber => subscriber.ReadAsync(context, total, run.Token)).ToArray();

            var writeClock = Stopwatch.StartNew();
            await WriteAtRateAsync(startedAt, run.Token);
            var writeElapsed = writeClock.Elapsed;
            await AwaitDeliveryAsync(reading, subscribers, total, run.Token);
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"subscribers={_profile.Subscribers} edits={total} write_seconds={writeElapsed.TotalSeconds:F1} " +
                $"achieved_eps={total / writeElapsed.TotalSeconds:F1} p50_ms={latencies.Percentile(0.50)} " +
                $"p95_ms={latencies.Percentile(0.95)} p99_ms={latencies.Percentile(0.99)} max_ms={latencies.Max} " +
                $"listener_pids={string.Join(',', listeners.Select(listener => listener.ProcessId))}"));
            foreach (var subscriber in subscribers)
            {
                subscriber.Received.Should().Be(total, "every subscriber receives every committed edit exactly once");
            }
            if (_profile.Subscribers > 0)
            {
                latencies.Percentile(0.95).Should().BeLessThanOrEqualTo((long)DeliveryTarget.TotalMilliseconds);
            }
            await AssertOneListenerPerTaskAsync(listeners, reconnectsBefore, listenerSessionsBefore);
        }
        finally
        {
            // Failed opens, writes and assertions must release every already-open stream too.
            await run.CancelAsync();
            foreach (var subscriber in subscribers)
            {
                subscriber.Dispose();
            }
            await Task.WhenAll(reading).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    // Opens the subscribers alternately on both tasks, adding each to the caller's list as it opens
    // so a failure part-way still leaves every already-open stream for the caller to dispose.
    private async Task OpenSubscribersAsync(
        HttpClient clientA, HttpClient clientB, List<Subscriber> subscribers, CancellationToken cancellationToken)
    {
        var checkpoints = new List<string>();
        for (var i = 0; i < _profile.Subscribers; i++)
        {
            if (i > 0 && i % 500 == 0)
            {
                checkpoints.Add($"at {i}: {await DescribeDatabaseSessionsAsync()}");
            }
            HttpResponseMessage response;
            try
            {
                response = await TwoHostFeatureHarness.OpenStreamAsync(i % 2 == 0 ? clientA : clientB, null, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw new InvalidOperationException(
                    $"Opening subscriber {i} of {_profile.Subscribers} failed; database sessions: {await DescribeDatabaseSessionsAsync()}; " +
                    $"earlier: {string.Join(" | ", checkpoints)}",
                    exception);
            }
            subscribers.Add(new Subscriber(response));
        }
    }

    // Waits up to the settle timeout for every subscriber to receive every edit, reporting the
    // received range when some fall short.
    private static async Task AwaitDeliveryAsync(
        Task[] reading, List<Subscriber> subscribers, int total, CancellationToken cancellationToken)
    {
        using var settle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        settle.CancelAfter(SettleTimeout);
        try
        {
            await Task.WhenAll(reading).WaitAsync(settle.Token);
        }
        catch (OperationCanceledException exception)
        {
            throw new TimeoutException(
                $"Subscribers received between {subscribers.Min(subscriber => subscriber.Received)} and " +
                $"{subscribers.Max(subscriber => subscriber.Received)} of {total} edits.", exception);
        }
    }

    // One listener connection per task for the whole run, independent of subscriber count.
    private async Task AssertOneListenerPerTaskAsync(
        RealtimeListener[] listeners, long[] reconnectsBefore, long listenerSessionsBefore)
    {
        listeners.Should().OnlyContain(listener => listener.IsConnected);
        listeners.Select(listener => listener.ProcessId).Should().OnlyHaveUniqueItems();
        listeners.Select(listener => listener.Reconnects).Should().Equal(reconnectsBefore);
        var listenerSessionsAfter = await CountListenerSessionsAsync();
        if (listenerSessionsAfter != listenerSessionsBefore)
        {
            listenerSessionsAfter.Should().Be(listenerSessionsBefore,
                $"listener connections must not grow with subscribers; sessions: {await DescribeDatabaseSessionsAsync()}");
        }
    }

    // Listener sessions on this database, compared before subscribers open and after delivery:
    // other test classes may hold their own listeners, but a regression that opens listener
    // connections per subscriber shows up as growth across the run.
    private async Task<long> CountListenerSessionsAsync()
    {
        await using var connection = new NpgsqlConnection(_taskA.DatabaseConnectionProvider.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND application_name = @name",
            connection);
        command.Parameters.AddWithValue("name", RealtimeListener.ApplicationName);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    // Who holds database connections: a subscriber that keeps one, or a pool that grows with
    // subscribers, shows up here directly.
    private async Task<string> DescribeDatabaseSessionsAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(_taskA.DatabaseConnectionProvider.GetConnectionString());
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT coalesce(application_name, '') || '/' || coalesce(state, '') || '=' || count(*) " +
                "FROM pg_stat_activity WHERE backend_type = 'client backend' GROUP BY application_name, state " +
                "UNION ALL SELECT 'max_connections=' || current_setting('max_connections')", connection);
            var rows = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add(reader.GetString(0));
            }
            return string.Join(", ", rows);
        }
        catch (NpgsqlException exception)
        {
            return $"unavailable ({exception.Message})";
        }
    }

    // Edits alternate between the two tasks at the profile rate. Each edit is issued on its own
    // schedule so one slow request does not lower the offered rate for the rest of the run.
    private async Task WriteAtRateAsync(long[] startedAt, CancellationToken cancellationToken)
    {
        using var writerA = _taskA.CreateAdminClient();
        using var writerB = _taskB.CreateAdminClient();
        var interval = TimeSpan.FromSeconds(1.0 / _profile.EditsPerSecond);
        var clock = Stopwatch.StartNew();
        var pending = new List<Task>(startedAt.Length);
        using var writing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            for (var ordinal = 0; ordinal < startedAt.Length; ordinal++)
            {
                var scheduledAt = interval * ordinal;
                var due = scheduledAt - clock.Elapsed;
                if (due > TimeSpan.Zero)
                {
                    await Task.Delay(due, writing.Token);
                }
                (clock.Elapsed - scheduledAt).Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1),
                    "the load client must sustain the configured issuance rate without delayed catch-up bursts");
                Volatile.Write(ref startedAt[ordinal], Stopwatch.GetTimestamp());
                pending.Add(TwoHostFeatureHarness.AddAsync(
                    ordinal % 2 == 0 ? writerA : writerB, $"{_marker}-{ordinal}", writing.Token));
            }
            await Task.WhenAll(pending);
        }
        finally
        {
            await writing.CancelAsync();
            await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static HttpClient SubscriberClient(WebAppFixture task)
    {
        var client = task.CreateAdminClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private sealed record DeliveryContext(string Marker, long[] StartedAt, LatencyHistogram Latencies);

    private sealed class Subscriber(HttpResponseMessage response) : IDisposable
    {
        public int Received { get; private set; }

        public async Task ReadAsync(DeliveryContext context, int expected, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            var lastGeneration = long.MinValue;
            var receivedOrdinals = new bool[expected];
            var stream = new MarkedFeatureStream(context.Marker);
            using var observation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            try
            {
                await foreach (var frame in stream.ReadAsync(reader, observation.Token))
                {
                    frame.Generation.Should().BeGreaterThan(lastGeneration, "delivery is once and in commit order");
                    lastGeneration = frame.Generation;
                    var ordinal = int.Parse(frame.Name.AsSpan(context.Marker.Length + 1), CultureInfo.InvariantCulture);
                    ordinal.Should().BeInRange(0, expected - 1);
                    receivedOrdinals[ordinal].Should().BeFalse("each marked edit is delivered exactly once");
                    receivedOrdinals[ordinal] = true;
                    context.StartedAt[ordinal].Should().BeGreaterThan(0, "an edit is issued before it is delivered");
                    context.Latencies.Record(Stopwatch.GetElapsedTime(Volatile.Read(ref context.StartedAt[ordinal]), frame.ReceivedAt));
                    if (++Received == expected)
                    {
                        observation.CancelAfter(TimeSpan.FromSeconds(1));
                    }
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && Received == expected)
            {
                return;
            }
            throw new EndOfStreamException($"The feature stream ended after {Received} of {expected} edits; {stream.Describe()}");
        }

        public void Dispose() => response.Dispose();
    }

    // Millisecond buckets up to 30 s; recording is lock-free across thousands of readers.
    private sealed class LatencyHistogram
    {
        private readonly long[] _buckets = new long[30_001];
        private long _count;
        private long _max;

        public long Max => Interlocked.Read(ref _max);

        public void Record(TimeSpan latency)
        {
            var milliseconds = Math.Clamp((long)Math.Ceiling(latency.TotalMilliseconds), 0, _buckets.Length - 1);
            Interlocked.Increment(ref _buckets[milliseconds]);
            Interlocked.Increment(ref _count);
            var seen = Interlocked.Read(ref _max);
            while (milliseconds > seen)
            {
                var prior = Interlocked.CompareExchange(ref _max, milliseconds, seen);
                if (prior == seen)
                {
                    return;
                }
                seen = prior;
            }
        }

        public long Percentile(double fraction)
        {
            var rank = (long)Math.Ceiling(Interlocked.Read(ref _count) * fraction);
            long cumulative = 0;
            for (var milliseconds = 0; milliseconds < _buckets.Length; milliseconds++)
            {
                cumulative += Interlocked.Read(ref _buckets[milliseconds]);
                if (cumulative >= rank && rank > 0)
                {
                    return milliseconds;
                }
            }
            return 0;
        }
    }

    private sealed record ScaleProfile(int Subscribers, int EditsPerSecond, TimeSpan Duration)
    {
        public int TotalEdits => (int)(EditsPerSecond * Duration.TotalSeconds);

        public static ScaleProfile FromEnvironment() => new(
            Read("HONUA_REALTIME_SCALE_SUBSCRIBERS", 2000),
            Math.Max(1, Read("HONUA_REALTIME_SCALE_EDITS_PER_SECOND", 50)),
            TimeSpan.FromSeconds(Math.Max(1, Read("HONUA_REALTIME_SCALE_SECONDS", 600))));

        private static int Read(string name, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
    }
}
