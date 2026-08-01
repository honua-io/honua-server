// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="QueryConcurrencyGate"/> admission control.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class QueryConcurrencyGateTests
{
    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WaitAsync_UnderLimit_ReturnsTrue()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 10 });

        var acquired = await gate.WaitAsync(CancellationToken.None);

        acquired.Should().BeTrue();
        gate.AvailableSlots.Should().Be(9);
        gate.CurrentLimit.Should().Be(10);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WaitAsync_AtLimit_ReturnsFalse()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 2,
            ConnectionAcquisitionTimeoutSeconds = 1
        });

        // Exhaust both slots
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();

        // Third attempt should time out
        var acquired = await gate.WaitAsync(CancellationToken.None);

        acquired.Should().BeFalse();
        gate.AvailableSlots.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_RestoresSlot()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        gate.AvailableSlots.Should().Be(0);

        gate.Release();
        gate.AvailableSlots.Should().Be(1);

        // Should be able to acquire again
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_MultipleSlots_RestoresAll()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 3 });

        // Acquire all 3
        for (var i = 0; i < 3; i++)
        {
            (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        }

        gate.AvailableSlots.Should().Be(0);

        gate.Release(3);
        gate.AvailableSlots.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WaitAsync_CancellationRequested_ThrowsOperationCanceled()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 5
        });

        // Exhaust the slot
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        var queued = gate.WaitAsync(cts.Token);
        queued.IsCompleted.Should().BeFalse("the second waiter is queued behind the held slot");
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await queued);

        gate.Release();
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task WaitAsync_WhenCancellationLosesRaceToAdmission_ReturnsTrue()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 5
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();

        using var cts = new CancellationTokenSource();
        var queued = gate.WaitAsync(cts.Token);
        queued.IsCompleted.Should().BeFalse("the second waiter is queued behind the held slot");

        gate.Release();
        await cts.CancelAsync();

        // Bounded by the gate's own ConnectionAcquisitionTimeoutSeconds rather than a test-side
        // wall-clock window, which a loaded CI runner can exceed while the gate is behaving correctly.
        (await queued).Should().BeTrue();
        gate.AvailableSlots.Should().Be(0);

        gate.Release();
        gate.AvailableSlots.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Constructor_ClampsLimitToConnectionPoolCeiling()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 10,
            MaxConnectionPoolSize = 3
        });

        gate.MaxLimit.Should().Be(3);
        gate.CurrentLimit.Should().Be(3);
        gate.AvailableSlots.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SetTargetLimit_Increase_AdmitsQueuedWaiter()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 3,
            MaxConnectionPoolSize = 3,
            ConnectionAcquisitionTimeoutSeconds = 5,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 1
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse();

        gate.SetTargetLimit(2);

        gate.GetSnapshot().QueuedWaiters.Should().Be(0, "raising the target drains the queue synchronously");
        (await queued).Should().BeTrue();
        gate.AvailableSlots.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task SetTargetLimit_Decrease_DoesNotCancelCurrentHolders()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 3,
            MaxConnectionPoolSize = 3,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 3
        });

        for (var i = 0; i < 3; i++)
        {
            (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        }

        gate.SetTargetLimit(1);

        gate.CurrentLimit.Should().Be(1);
        gate.AvailableSlots.Should().Be(0);

        gate.Release();
        gate.AvailableSlots.Should().Be(0);

        gate.Release();
        gate.AvailableSlots.Should().Be(0);

        gate.Release();
        gate.AvailableSlots.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_WithSlowLeaseDuration_ReducesAdaptiveLimit()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 4,
            MaxConnectionPoolSize = 4,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 4,
            AdaptiveConcurrencyTargetDurationMs = 10,
            AdaptiveConcurrencyUpdateIntervalMs = 0
        });

        for (var i = 0; i < 4; i++)
        {
            (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        }

        gate.Release(TimeSpan.FromMilliseconds(100));

        gate.CurrentLimit.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_WithModerateSlowLeaseAndQueuePressure_DoesNotReduceAdaptiveLimit()
    {
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 4,
            MaxConnectionPoolSize = 4,
            ConnectionAcquisitionTimeoutSeconds = 5,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 2,
            AdaptiveConcurrencyInitialQueries = 4,
            AdaptiveConcurrencyTargetDurationMs = 100,
            AdaptiveConcurrencyUpdateIntervalMs = 0
        });

        for (var i = 0; i < 4; i++)
        {
            (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        }

        var queued = Enumerable.Range(0, 4)
            .Select(_ => gate.WaitAsync(CancellationToken.None))
            .ToArray();

        gate.GetSnapshot().QueuedWaiters.Should().Be(4);

        gate.Release(TimeSpan.FromMilliseconds(200));

        gate.CurrentLimit.Should().Be(4);

        gate.Release(4);
        (await Task.WhenAll(queued)).Should().OnlyContain(acquired => acquired);
        gate.Release(4);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_WithQueuedWaiter_ReportsQueueWaitEwma()
    {
        var clock = new ManualTimeProvider();
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 2,
            MaxConnectionPoolSize = 2,
            ConnectionAcquisitionTimeoutSeconds = 5,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 1,
            AdaptiveConcurrencyTargetDurationMs = 100,
            AdaptiveConcurrencyUpdateIntervalMs = 0
        }, clock);

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse("the second waiter is queued behind the held slot");
        clock.Advance(TimeSpan.FromMilliseconds(125));

        gate.Release(TimeSpan.FromMilliseconds(1));
        (await queued).Should().BeTrue();

        var snapshot = gate.GetSnapshot();
        snapshot.QueueWaitEwmaMs.Should().BeApproximately(125, 0.01);

        gate.Release();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_WithFastSaturatedLease_IncreasesAdaptiveLimitAndDrainsWaiter()
    {
        var clock = new ManualTimeProvider();
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 4,
            MaxConnectionPoolSize = 4,
            ConnectionAcquisitionTimeoutSeconds = 5,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 1,
            AdaptiveConcurrencyTargetDurationMs = 100,
            AdaptiveConcurrencyUpdateIntervalMs = 0
        }, clock);

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse();

        gate.Release(TimeSpan.FromMilliseconds(1));

        // Release applies the adaptive adjustment and drains the queue synchronously under the gate
        // lock, so the transition is asserted from gate state instead of from a wall-clock window
        // around the waiter's continuation, which a loaded CI runner can delay past any fixed budget.
        var snapshot = gate.GetSnapshot();
        snapshot.CurrentLimit.Should().Be(2, "a fast lease released while saturated raises the adaptive limit");
        snapshot.LastAdjustmentDirection.Should().Be("increase");
        snapshot.QueuedWaiters.Should().Be(0, "raising the limit drains the queued waiter");
        snapshot.InFlight.Should().Be(1, "the drained waiter now holds the slot");

        // The waiter's own task must still observe admission. No test-side timeout: the gate's own
        // ConnectionAcquisitionTimeoutSeconds bounds this await, so a regression surfaces as a false
        // result rather than as a timing-dependent TimeoutException.
        (await queued).Should().BeTrue();
        gate.CurrentLimit.Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSnapshot_ReportsAdaptiveAdmissionState()
    {
        var clock = new ManualTimeProvider();
        var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 4,
            MaxConnectionPoolSize = 4,
            ConnectionAcquisitionTimeoutSeconds = 5,
            AdaptiveConcurrencyEnabled = true,
            AdaptiveConcurrencyMinQueries = 1,
            AdaptiveConcurrencyInitialQueries = 1,
            AdaptiveConcurrencyTargetDurationMs = 100,
            AdaptiveConcurrencyUpdateIntervalMs = 0
        }, clock);

        var snapshot = gate.GetSnapshot();
        snapshot.AdaptiveEnabled.Should().BeTrue();
        snapshot.CurrentLimit.Should().Be(1);
        snapshot.MinLimit.Should().Be(1);
        snapshot.MaxLimit.Should().Be(4);
        snapshot.TargetDurationMs.Should().Be(100);
        snapshot.LastAdjustmentDirection.Should().Be("none");

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);

        snapshot = gate.GetSnapshot();
        snapshot.InFlight.Should().Be(1);
        snapshot.AvailableSlots.Should().Be(0);
        snapshot.QueuedWaiters.Should().Be(1);
        clock.Advance(TimeSpan.FromMilliseconds(75));

        gate.Release(TimeSpan.FromMilliseconds(1));
        (await queued).Should().BeTrue();

        snapshot = gate.GetSnapshot();
        snapshot.CurrentLimit.Should().Be(2);
        snapshot.InFlight.Should().Be(1);
        snapshot.AvailableSlots.Should().Be(1);
        snapshot.QueuedWaiters.Should().Be(0);
        snapshot.DurationEwmaMs.Should().BeApproximately(1, 0.01);
        snapshot.QueueWaitEwmaMs.Should().BeApproximately(75, 0.01);
        snapshot.AdjustmentCount.Should().Be(1);
        snapshot.LastAdjustmentDirection.Should().Be("increase");

        gate.Release();
    }

    // ------------------------------------------------------------------
    // Runtime-tunable admission seam (IRuntimeTunableAdmissionGate, #2561)
    // ------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void TrySetLimit_WithinRange_AppliesTransientTarget()
    {
        using var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 10 });

        gate.TrySetLimit(3, out var error).Should().BeTrue();

        error.Should().BeNull();
        gate.CurrentLimit.Should().Be(3);
        gate.MaxLimit.Should().Be(10, "the configured ceiling never changes");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void TrySetLimit_AboveMax_RejectsWithoutChange()
    {
        using var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 10 });

        gate.TrySetLimit(11, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        gate.CurrentLimit.Should().Be(10);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void TrySetLimit_BelowFloor_RejectsWithoutChange()
    {
        using var gate = new QueryConcurrencyGate(new ConnectionLimits { MaxConcurrentQueries = 10 });

        gate.TrySetLimit(0, out var error).Should().BeFalse();

        error.Should().NotBeNullOrWhiteSpace();
        gate.CurrentLimit.Should().Be(10);
        gate.MinLimit.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task TrySetLimit_RaisingLimit_DrainsQueuedWaiters()
    {
        using var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 4,
            ConnectionAcquisitionTimeoutSeconds = 5
        });

        gate.TrySetLimit(1, out _).Should().BeTrue();
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse("the tuned-down gate must queue the second waiter");

        gate.TrySetLimit(2, out _).Should().BeTrue();

        (await queued).Should().BeTrue(
            "raising the admission target must admit queued waiters");
    }

    // ------------------------------------------------------------------
    // Admission pressure signal (DatabaseAdmissionPressure, #2805)
    // ------------------------------------------------------------------

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetPressure_UnderExhaustion_ReportsSaturationAndWindowedTimeout()
    {
        using var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1,
        });

        // Lease the only slot, then a second acquisition times out — the real exhaustion path.
        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        (await gate.WaitAsync(CancellationToken.None)).Should().BeFalse("the pool is exhausted");

        var pressure = gate.GetPressure();

        pressure.HasUtilization.Should().BeTrue();
        pressure.CurrentLimit.Should().Be(1);
        pressure.InFlight.Should().Be(1);
        pressure.Utilization.Should().Be(1.0);
        pressure.WindowedAcquisitionTimeouts.Should().BeGreaterThanOrEqualTo(1);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetPressure_AcquisitionTimeouts_AreEvictedAfterWindow()
    {
        var clock = new ManualTimeProvider();
        using var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 1,
        }, clock);

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        (await gate.WaitAsync(CancellationToken.None)).Should().BeFalse("the pool is exhausted");

        // The timeout was stamped at the manual clock's current instant.
        gate.GetPressure().WindowedAcquisitionTimeouts.Should().Be(1);

        // Advance past the trailing window; the timeout must age out so the finding does not latch forever.
        clock.Advance(TimeSpan.FromSeconds(61));

        gate.GetPressure().WindowedAcquisitionTimeouts.Should().Be(0);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
