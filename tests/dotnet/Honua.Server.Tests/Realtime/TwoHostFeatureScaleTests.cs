// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using FluentAssertions;
using Honua.Server.Features.Realtime;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit.Abstractions;

namespace Honua.Server.Tests.Realtime;

/// <summary>
/// Specification realtime-cross-node-rc3 section 12 item 9: two tasks on one database, subscribers
/// split across both, edits written through both, every subscriber receiving every edit once and in
/// commit order with p95 delivery within one second and one listener connection per task.
/// </summary>
/// <remarks>
/// Opt in with <c>HONUA_REALTIME_SCALE=1</c>. Defaults are the specification profile (2,000
/// subscribers, 50 edits per second, 600 seconds); <c>HONUA_REALTIME_SCALE_SUBSCRIBERS</c>,
/// <c>HONUA_REALTIME_SCALE_EDITS_PER_SECOND</c> and <c>HONUA_REALTIME_SCALE_SECONDS</c> shorten a
/// smoke run. The database CPU criterion compares two runs of this test on the same database host,
/// one with <c>HONUA_REALTIME_SCALE_SUBSCRIBERS=0</c>; the database host is measured outside the test.
/// </remarks>
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
            ["FeatureStreaming:MaxConcurrentSessions"] = (perTask + 16).ToString(CultureInfo.InvariantCulture),
            // One admin key writes every edit; keep the limiter on but sized above the offered rate.
            ["RateLimiting:GlobalRequestsPerMinute"] = "100000"
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
        var startedAt = new long[total];
        var latencies = new LatencyHistogram();

        using var clientA = SubscriberClient(_taskA);
        using var clientB = SubscriberClient(_taskB);
        var subscribers = new List<Subscriber>(_profile.Subscribers);
        for (var i = 0; i < _profile.Subscribers; i++)
        {
            var response = await TwoHostFeatureHarness.OpenStreamAsync(i % 2 == 0 ? clientA : clientB, null, run.Token);
            subscribers.Add(new Subscriber(response));
        }
        var context = new DeliveryContext(_marker, startedAt, latencies);
        var reading = subscribers.Select(subscriber => subscriber.ReadAsync(context, total, run.Token)).ToArray();

        var writeClock = Stopwatch.StartNew();
        await WriteAtRateAsync(startedAt, run.Token);
        var writeElapsed = writeClock.Elapsed;
        using (var settle = CancellationTokenSource.CreateLinkedTokenSource(run.Token))
        {
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

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"subscribers={_profile.Subscribers} edits={total} write_seconds={writeElapsed.TotalSeconds:F1} " +
            $"achieved_eps={total / writeElapsed.TotalSeconds:F1} p50_ms={latencies.Percentile(0.50)} " +
            $"p95_ms={latencies.Percentile(0.95)} p99_ms={latencies.Percentile(0.99)} max_ms={latencies.Max} " +
            $"listener_pids={string.Join(',', listeners.Select(listener => listener.ProcessId))}"));
        foreach (var subscriber in subscribers)
        {
            subscriber.Received.Should().Be(total, "every subscriber receives every committed edit exactly once");
            subscriber.Dispose();
        }
        if (_profile.Subscribers > 0)
        {
            latencies.Percentile(0.95).Should().BeLessThanOrEqualTo((long)DeliveryTarget.TotalMilliseconds);
        }
        // One listener connection per task for the whole run, independent of subscriber count.
        listeners.Should().OnlyContain(listener => listener.IsConnected);
        listeners.Select(listener => listener.ProcessId).Should().OnlyHaveUniqueItems();
        listeners.Select(listener => listener.Reconnects).Should().Equal(reconnectsBefore);
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
        for (var ordinal = 0; ordinal < startedAt.Length; ordinal++)
        {
            var due = interval * ordinal - clock.Elapsed;
            if (due > TimeSpan.Zero)
            {
                await Task.Delay(due, cancellationToken);
            }
            startedAt[ordinal] = Stopwatch.GetTimestamp();
            pending.Add(TwoHostFeatureHarness.AddAsync(
                ordinal % 2 == 0 ? writerA : writerB, $"{_marker}-{ordinal}", cancellationToken));
        }
        await Task.WhenAll(pending);
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

        // Strictly ascending generations rule out duplicates and reordering; with that, a full
        // count rules out loss. Per-subscriber sets of 30,000 ids would cost far more memory.
        public async Task ReadAsync(DeliveryContext context, int expected, CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellationToken));
            var lastGeneration = long.MinValue;
            var stream = new MarkedFeatureStream(context.Marker);
            await foreach (var frame in stream.ReadAsync(reader, cancellationToken))
            {
                frame.Generation.Should().BeGreaterThan(lastGeneration, "delivery is once and in commit order");
                lastGeneration = frame.Generation;
                var ordinal = int.Parse(frame.Name.AsSpan(context.Marker.Length + 1), CultureInfo.InvariantCulture);
                context.Latencies.Record(Stopwatch.GetElapsedTime(Volatile.Read(ref context.StartedAt[ordinal]), frame.ReceivedAt));
                if (++Received == expected)
                {
                    return;
                }
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
            var milliseconds = Math.Clamp((long)latency.TotalMilliseconds, 0, _buckets.Length - 1);
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
