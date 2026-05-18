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

        // Cancel while waiting
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => gate.WaitAsync(cts.Token));

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
        await Task.Delay(50);

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
        await Task.Delay(50);
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
        await Task.Delay(50);

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
        });

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);

        gate.Release(TimeSpan.FromMilliseconds(1));
        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

        var snapshot = gate.GetSnapshot();
        snapshot.QueueWaitEwmaMs.Should().BeGreaterThan(0);

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
        await Task.Delay(50);
        queued.IsCompleted.Should().BeFalse();

        gate.Release(TimeSpan.FromMilliseconds(1));

        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
        gate.CurrentLimit.Should().Be(2);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task GetSnapshot_ReportsAdaptiveAdmissionState()
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

        var snapshot = gate.GetSnapshot();
        snapshot.AdaptiveEnabled.Should().BeTrue();
        snapshot.CurrentLimit.Should().Be(1);
        snapshot.MinLimit.Should().Be(1);
        snapshot.MaxLimit.Should().Be(4);
        snapshot.TargetDurationMs.Should().Be(100);
        snapshot.LastAdjustmentDirection.Should().Be("none");

        (await gate.WaitAsync(CancellationToken.None)).Should().BeTrue();
        var queued = gate.WaitAsync(CancellationToken.None);
        await Task.Delay(50);

        snapshot = gate.GetSnapshot();
        snapshot.InFlight.Should().Be(1);
        snapshot.AvailableSlots.Should().Be(0);
        snapshot.QueuedWaiters.Should().Be(1);

        gate.Release(TimeSpan.FromMilliseconds(1));
        (await queued.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();

        snapshot = gate.GetSnapshot();
        snapshot.CurrentLimit.Should().Be(2);
        snapshot.InFlight.Should().Be(1);
        snapshot.AvailableSlots.Should().Be(1);
        snapshot.QueuedWaiters.Should().Be(0);
        snapshot.DurationEwmaMs.Should().BeApproximately(1, 0.01);
        snapshot.QueueWaitEwmaMs.Should().BeGreaterThan(0);
        snapshot.AdjustmentCount.Should().Be(1);
        snapshot.LastAdjustmentDirection.Should().Be("increase");

        gate.Release();
    }
}
