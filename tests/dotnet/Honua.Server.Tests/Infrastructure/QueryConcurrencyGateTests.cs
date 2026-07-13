// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Infrastructure.Abstractions;
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

        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
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

        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
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
        (await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(1))).Should().OnlyContain(acquired => acquired);
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
        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

        var snapshot = gate.GetSnapshot();
        snapshot.QueueWaitEwmaMs.Should().BeApproximately(125, 0.01);

        gate.Release();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task Release_WithFastSaturatedLease_IncreasesAdaptiveLimitAndDrainsWaiter()
    {
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
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse();

        gate.Release(TimeSpan.FromMilliseconds(1));

        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
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
        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

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

        (await queued.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue(
            "raising the admission target must admit queued waiters");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetPressureReading_WhenSaturated_ReportsFullUtilizationAndQueuedWaiter()
    {
        // honua-server#2805: the db-pressure findings rule reads the gate's real saturation signal.
        using var gate = new QueryConcurrencyGate(new ConnectionLimits
        {
            MaxConcurrentQueries = 1,
            ConnectionAcquisitionTimeoutSeconds = 5
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        queued.IsCompleted.Should().BeFalse("the single slot is taken, so the second waiter must queue");

        var reading = ((IDatabaseAdmissionPressureSource)gate).GetPressureReading();
        reading.HasUtilization.Should().BeTrue();
        reading.Utilization.Should().Be(1.0);
        reading.InFlight.Should().Be(1);
        reading.CurrentLimit.Should().Be(1);
        reading.QueuedWaiters.Should().Be(1);

        gate.Release();
        (await queued.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetPressureReading_AcquisitionTimeoutSignal_IsWindowedNotLifetimeMonotonic()
    {
        // honua-server#2805: the timeout signal must be windowed, so a burst of timeouts relaxes
        // back to zero once the window elapses instead of latching a Critical finding until restart.
        var clock = new ManualTimeProvider();
        using var gate = new QueryConcurrencyGate(
            new ConnectionLimits
            {
                MaxConcurrentQueries = 1,
                ConnectionAcquisitionTimeoutSeconds = 1
            },
            clock,
            timeoutWindow: TimeSpan.FromSeconds(30));

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        // The single slot is taken; this attempt waits the full acquisition timeout and records a
        // windowed timeout at the current (manual) clock position.
        (await gate.WaitAsync(CancellationToken.None)).Should().BeFalse();

        ((IDatabaseAdmissionPressureSource)gate).GetPressureReading()
            .AcquisitionTimeoutsInWindow.Should().Be(1, "a genuine acquisition timeout was just recorded");

        // Advance past the rolling window: the timeout must age out.
        clock.Advance(TimeSpan.FromSeconds(31));
        ((IDatabaseAdmissionPressureSource)gate).GetPressureReading()
            .AcquisitionTimeoutsInWindow.Should().Be(0, "the timeout must fall out of the rolling window");
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref _timestamp, elapsed.Ticks);
    }
}
