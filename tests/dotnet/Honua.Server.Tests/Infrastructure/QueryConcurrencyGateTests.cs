// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Postgres.Features.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Infrastructure;

/// <summary>
/// Tests for <see cref="QueryConcurrencyGate"/> semaphore-based admission control.
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
    }
}
