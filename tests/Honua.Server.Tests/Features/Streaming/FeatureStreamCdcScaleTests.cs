// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Events;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Streaming;

/// <summary>
/// Scale test for the CDC-to-streaming fan-out pipeline.
/// Validates that 100 concurrent subscribers receive 1000 edits/sec
/// with &lt;500ms p95 delivery latency.
/// </summary>
[Protocol(Protocols.Streaming)]
[Operation(Operations.Streaming)]
public sealed class FeatureStreamCdcScaleTests : IDisposable
{
    private const int SubscriberCount = 100;
    private const int EditsPerSecond = 1000;
    private const int DurationSeconds = 3;
    private const int TotalEdits = EditsPerSecond * DurationSeconds;
    private const double MaxP95LatencyMs = 500.0;

    private readonly FeatureStreamSessionManager _sessionManager;
    private readonly InMemoryFeatureChangeEventStore _store;
    private readonly FeatureStreamPublisher _publisher;

    public FeatureStreamCdcScaleTests()
    {
        var storeOptions = Options.Create(new FeatureChangeEventOptions { MaxRetainedEvents = TotalEdits + 1000 });
        _store = new InMemoryFeatureChangeEventStore(storeOptions, null, null);

        var streamOptions = Options.Create(new FeatureStreamOptions { MaxBufferPerConnection = TotalEdits + 256 });
        _sessionManager = new FeatureStreamSessionManager(streamOptions, NullLogger<FeatureStreamSessionManager>.Instance);

        _publisher = new FeatureStreamPublisher(
            _store,
            _sessionManager,
            NullLogger<FeatureStreamPublisher>.Instance);
    }

    public void Dispose() => _sessionManager.Dispose();

    [UnitTest]
    [Trait("Category", "Performance")]
    public async Task CdcFanOut_100Subscribers_1000EditsPerSec_Under500msP95()
    {
        // Arrange: create 100 subscriber sessions.
        var sessions = new List<FeatureStreamSession>(SubscriberCount);
        for (var i = 0; i < SubscriberCount; i++)
        {
            var session = _sessionManager.CreateSession("WebSocket", $"sub-{i}");
            _sessionManager.MarkDrainStarted(session.SessionId);
            _sessionManager.ClearDrainGrace(session.SessionId);
            sessions.Add(session);
        }

        _sessionManager.SessionCount.Should().Be(SubscriberCount);

        // Track publish timestamps for latency measurement.
        var publishTimestamps = new ConcurrentDictionary<long, long>(); // cursor -> stopwatch ticks

        // Start consumer tasks that drain each session's channel.
        var deliveryLatencies = new ConcurrentBag<double>();
        var consumedCounts = new int[SubscriberCount];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var consumerTasks = new Task[SubscriberCount];
        for (var i = 0; i < SubscriberCount; i++)
        {
            var idx = i;
            var session = sessions[idx];
            consumerTasks[idx] = Task.Run(async () =>
            {
                try
                {
                    await foreach (var msg in session.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                    {
                        if (msg.IsHeartbeat)
                        {
                            continue;
                        }

                        var now = Stopwatch.GetTimestamp();
                        if (publishTimestamps.TryGetValue(msg.Envelope.Cursor, out var publishTick))
                        {
                            var latencyMs = (double)(now - publishTick) / Stopwatch.Frequency * 1000.0;
                            deliveryLatencies.Add(latencyMs);
                        }

                        consumedCounts[idx]++;
                        if (consumedCounts[idx] >= TotalEdits)
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout.
                }
            });
        }

        // Act: publish events at the target rate.
        var interval = TimeSpan.FromMilliseconds(1000.0 / EditsPerSecond);
        var overallSw = Stopwatch.StartNew();

        for (var i = 0; i < TotalEdits; i++)
        {
            var publishTick = Stopwatch.GetTimestamp();

            await _publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "scale-svc",
                LayerId = i % 5, // Spread across 5 layers.
                ObjectId = i,
                Operation = i % 3 == 0 ? "create" : i % 3 == 1 ? "update" : "delete",
                Protocol = "scale-test",
                RequestId = $"req-{i}",
                GeometryChanged = i % 2 == 0
            }).ConfigureAwait(false);

            // Record publish time after the publisher completes (includes store + broadcast).
            var storedEvents = await _store.QueryAsync(null, null, null, 1).ConfigureAwait(false);
            if (storedEvents.Count > 0)
            {
                // Use the last published cursor (store is append-only, so the latest has highest cursor).
                var latest = await _store.QueryAsync((long)(i), null, null, 1).ConfigureAwait(false);
                if (latest.Count > 0)
                {
                    publishTimestamps.TryAdd(latest[0].Cursor, publishTick);
                }
            }

            // Pace to target rate — subtract time already spent.
            var targetElapsed = interval * (i + 1);
            var actualElapsed = overallSw.Elapsed;
            if (actualElapsed < targetElapsed)
            {
                await Task.Delay(targetElapsed - actualElapsed).ConfigureAwait(false);
            }
        }

        var publishDuration = overallSw.Elapsed;

        // Wait for all consumers to finish.
        var allDone = Task.WhenAll(consumerTasks);
        await Task.WhenAny(allDone, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);

        // Assert: all subscribers received all events.
        for (var i = 0; i < SubscriberCount; i++)
        {
            consumedCounts[i].Should().Be(TotalEdits,
                $"subscriber {i} should receive all {TotalEdits} events");
        }

        // Assert: p95 delivery latency < 500ms.
        var latencies = deliveryLatencies.OrderBy(static l => l).ToArray();
        latencies.Should().NotBeEmpty("delivery latencies should be recorded");

        var p95Index = (int)Math.Ceiling(latencies.Length * 0.95) - 1;
        var p95Latency = latencies[Math.Max(0, p95Index)];
        p95Latency.Should().BeLessThan(MaxP95LatencyMs,
            $"p95 delivery latency should be under {MaxP95LatencyMs}ms (actual: {p95Latency:F2}ms)");

        // Cleanup.
        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }

    [UnitTest]
    [Trait("Category", "Performance")]
    public async Task CdcFanOut_FilteredSubscribers_ReceiveOnlyMatchingLayerDeltas()
    {
        // Arrange: create subscribers with different layer filters.
        var layer0Filter = new StreamSubscriptionFilter(layerIds: [0]);
        var layer1Filter = new StreamSubscriptionFilter(layerIds: [1]);

        using var session0 = _sessionManager.CreateSession("WebSocket", "layer-0", layer0Filter);
        _sessionManager.MarkDrainStarted(session0.SessionId);
        _sessionManager.ClearDrainGrace(session0.SessionId);

        using var session1 = _sessionManager.CreateSession("WebSocket", "layer-1", layer1Filter);
        _sessionManager.MarkDrainStarted(session1.SessionId);
        _sessionManager.ClearDrainGrace(session1.SessionId);

        using var sessionAll = _sessionManager.CreateSession("WebSocket", "all-layers");
        _sessionManager.MarkDrainStarted(sessionAll.SessionId);
        _sessionManager.ClearDrainGrace(sessionAll.SessionId);

        // Act: publish events for layers 0, 1, and 2.
        for (var i = 0; i < 30; i++)
        {
            await _publisher.PublishAsync(new FeatureChangeEventRequest
            {
                ServiceId = "filter-svc",
                LayerId = i % 3,
                ObjectId = i,
                Operation = "create",
                Protocol = "test",
                RequestId = $"req-filter-{i}"
            }).ConfigureAwait(false);
        }

        // Assert: layer-0 subscriber only gets layer 0 events.
        var layer0Count = 0;
        while (session0.Reader.TryRead(out var msg))
        {
            if (!msg.IsHeartbeat)
            {
                msg.Envelope.LayerId.Should().Be(0);
                layer0Count++;
            }
        }

        layer0Count.Should().Be(10, "layer-0 subscriber should receive exactly 10 events (30/3)");

        // Assert: layer-1 subscriber only gets layer 1 events.
        var layer1Count = 0;
        while (session1.Reader.TryRead(out var msg1))
        {
            if (!msg1.IsHeartbeat)
            {
                msg1.Envelope.LayerId.Should().Be(1);
                layer1Count++;
            }
        }

        layer1Count.Should().Be(10, "layer-1 subscriber should receive exactly 10 events (30/3)");

        // Assert: unfiltered subscriber gets all 30 events.
        var allCount = 0;
        while (sessionAll.Reader.TryRead(out var msgAll))
        {
            if (!msgAll.IsHeartbeat)
            {
                allCount++;
            }
        }

        allCount.Should().Be(30, "unfiltered subscriber should receive all 30 events");
    }
}
