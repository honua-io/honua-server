// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using BenchmarkDotNet.Attributes;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;

namespace Honua.Benchmarks;

/// <summary>
/// Admission-control fast-path benchmarks. Every database query in the
/// Postgres stack passes through <see cref="QueryConcurrencyGate.WaitAsync"/>
/// and <see cref="QueryConcurrencyGate.Release(int)"/>, so the synchronous
/// uncontended cost of acquiring and releasing a slot is multiplied across
/// the entire query throughput. We benchmark three shapes that do NOT
/// require a live database:
/// <list type="bullet">
///   <item>The uncontended fast path (slot available, no waiters).</item>
///   <item>A burst that acquires and releases all configured slots in
///         sequence — modelling a request flight that briefly saturates
///         the gate and immediately drains it.</item>
///   <item>The adaptive-controller path (<c>Release(TimeSpan)</c>), which
///         additionally folds the lease duration into the EWMA tracker
///         and may adjust the target limit.</item>
/// </list>
/// Contention with multiple readers is intentionally out of scope here —
/// real DB load is exercised by the integration soak rig.
/// </summary>
[MemoryDiagnoser]
[BenchmarkCategory(Categories.ConnectionPool)]
public class ConnectionPoolBenchmarks
{
    private QueryConcurrencyGate _fastPathGate = null!;
    private QueryConcurrencyGate _burstGate = null!;
    private QueryConcurrencyGate _adaptiveGate = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Static gate sized so the fast-path benchmark never queues: each
        // WaitAsync should hit the uncontended branch that just increments
        // the in-flight counter under the lock.
        _fastPathGate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 64,
            ConnectionAcquisitionTimeoutSeconds = 30,
            AdaptiveConcurrencyEnabled = false,
        });

        // Smaller, non-adaptive gate so the burst benchmark walks the full
        // saturate-and-drain cycle without triggering adaptive sizing.
        _burstGate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 8,
            ConnectionAcquisitionTimeoutSeconds = 30,
            AdaptiveConcurrencyEnabled = false,
        });

        // Adaptive gate uses the lease-aware Release overload that updates
        // the EWMA tracker. We isolate the adaptive overhead so the diff
        // versus the static gate captures only the controller cost.
        _adaptiveGate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 64,
            ConnectionAcquisitionTimeoutSeconds = 30,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 4,
            AdaptiveConcurrencyMaxQueries = 64,
            AdaptiveConcurrencyInitialQueries = 16,
            AdaptiveConcurrencyTargetDurationMs = 50,
            AdaptiveConcurrencyUpdateIntervalMs = 1000,
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _fastPathGate.Dispose();
        _burstGate.Dispose();
        _adaptiveGate.Dispose();
    }

    [Benchmark(Baseline = true, Description = "WaitAsync + Release fast path (uncontended)")]
    public async Task<bool> AcquireReleaseFastPath()
    {
        // WaitAsync should complete synchronously when a slot is free; the
        // Release call returns the slot immediately so the next iteration
        // sees the same uncontended state.
        var acquired = await _fastPathGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _fastPathGate.Release();
        return acquired;
    }

    [Benchmark(Description = "Saturate + drain 8 slots back-to-back")]
    public int AcquireReleaseBurst()
    {
        // Acquire every available slot, then release them in FIFO order.
        // Uses the sync-completing fast path on each WaitAsync since no
        // waiters are queued — measures the per-iteration lock overhead at
        // the bursty edge of saturation.
        var acquired = 0;
        for (var i = 0; i < 8; i++)
        {
            var task = _burstGate.WaitAsync(CancellationToken.None);
            if (task.IsCompletedSuccessfully && task.Result)
            {
                acquired++;
            }
        }

        _burstGate.Release(acquired);
        return acquired;
    }

    [Benchmark(Description = "Adaptive Release(TimeSpan) updates EWMA")]
    public async Task<bool> AcquireAdaptiveRelease()
    {
        // Exercises the adaptive controller code path: the lease-aware
        // Release overload folds the duration into the EWMA tracker.
        var acquired = await _adaptiveGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _adaptiveGate.Release(TimeSpan.FromMilliseconds(10));
        return acquired;
    }
}
