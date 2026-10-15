// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Drives the shipped routing pair budget and solve-capacity gate. The session
/// factory fails if a solve reaches the database, so an over-budget request must
/// be rejected by <see cref="PgRoutingProvider"/> first.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public sealed class RoutingSolveBudgetTests
{
    [Fact]
    public async Task SolveClosestFacility_OverPairBudget_RejectsBeforeOpen()
    {
        var configuration = new RoutingConfiguration();
        var factory = new ThrowingSessionFactory();
        var provider = new PgRoutingProvider(factory, NullLogger<PgRoutingProvider>.Instance);
        var facilities = configuration.MaxClosestFacilities;
        var incidents = configuration.MaxSolvePairs / facilities + 1;
        var request = new ClosestFacilitySolveRequest(Points(incidents), Points(facilities), facilities);

        var act = () => provider.SolveClosestFacilityAsync(request);

        await act.Should().ThrowAsync<RoutingSolveBudgetExceededException>();
        factory.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task SolveClosestFacility_TargetAboveConfiguredCap_RejectsBeforeOpen()
    {
        var configuration = new RoutingConfiguration();
        var factory = new ThrowingSessionFactory();
        var provider = new PgRoutingProvider(factory, NullLogger<PgRoutingProvider>.Instance);
        var request = new ClosestFacilitySolveRequest(
            Points(1),
            Points(1),
            configuration.MaxClosestFacilities + 1);

        var act = () => provider.SolveClosestFacilityAsync(request);

        await act.Should().ThrowAsync<RoutingSolveBudgetExceededException>();
        factory.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task SolveOdCostMatrix_OverPairBudget_RejectsBeforeOpen()
    {
        var configuration = new RoutingConfiguration();
        var factory = new ThrowingSessionFactory();
        var provider = new PgRoutingProvider(factory, NullLogger<PgRoutingProvider>.Instance);
        var destinations = configuration.MaxDestinations;
        var origins = configuration.MaxSolvePairs / destinations + 1;
        var request = new OdCostMatrixSolveRequest(Points(origins), Points(destinations));

        var act = () => provider.SolveOdCostMatrixAsync(request);

        await act.Should().ThrowAsync<RoutingSolveBudgetExceededException>();
        factory.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task SolveLocationAllocation_OverPairBudget_RejectsBeforeOpen()
    {
        var configuration = new RoutingConfiguration();
        var factory = new ThrowingSessionFactory();
        var provider = new PgRoutingProvider(factory, NullLogger<PgRoutingProvider>.Instance);
        var demands = configuration.MaxDemandPoints;
        var facilities = configuration.MaxSolvePairs / demands + 1;
        var request = new LocationAllocationSolveRequest(
            Points(facilities),
            Points(demands).Select(point => new DemandPoint(point)).ToArray());

        var act = () => provider.SolveLocationAllocationAsync(request);

        await act.Should().ThrowAsync<RoutingSolveBudgetExceededException>();
        factory.Opened.Should().BeFalse();
    }

    [Fact]
    public async Task Acquire_FullGate_ThrowsCapacityExceeded()
    {
        using var limiter = new RoutingSolveCapacityLimiter(
            RoutingSolveCapacityLimiter.DefaultMaxConcurrentSolves,
            TimeSpan.Zero);
        var held = new IDisposable[RoutingSolveCapacityLimiter.DefaultMaxConcurrentSolves];
        for (var i = 0; i < held.Length; i++)
        {
            held[i] = await limiter.AcquireAsync(CancellationToken.None);
        }

        var act = async () => await limiter.AcquireAsync(CancellationToken.None);

        await act.Should().ThrowAsync<RoutingSolveCapacityExceededException>()
            .WithMessage(RoutingSolveCapacityLimiter.CapacityExceededMessage);
        foreach (var lease in held)
        {
            lease.Dispose();
        }
    }

    private static RoutePoint[] Points(int count)
        => Enumerable.Range(0, count).Select(i => new RoutePoint(i, 0)).ToArray();

    private sealed class ThrowingSessionFactory : IDatabaseSessionFactory
    {
        public bool Opened { get; private set; }

        public Task<IDatabaseSession> OpenAsync(CancellationToken cancellationToken = default)
        {
            Opened = true;
            throw new InvalidOperationException("OpenAsync must not run for an over-budget solve.");
        }

        public Task<IDatabaseSession> OpenTransactionAsync(
            IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
