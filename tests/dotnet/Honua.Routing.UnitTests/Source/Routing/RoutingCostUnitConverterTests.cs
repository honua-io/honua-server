// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Unit tests (no database) for <see cref="RoutingCostUnitConverter"/>, the single
/// declared contract that maps the topology's raw pgRouting cost weight onto the
/// travel-time minutes the routing request/response surface speaks in (#2008).
/// </summary>
public sealed class RoutingCostUnitConverterTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(5.0, 5.0)]
    [InlineData(42.5, 42.5)]
    public void CostToMinutes_Minutes_IsIdentity(double cost, double expectedMinutes)
    {
        RoutingCostUnitConverter.CostToMinutes(cost, RoutingCostUnit.Minutes)
            .Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(60.0, 1.0)]
    [InlineData(120.0, 2.0)]
    [InlineData(90.0, 1.5)]
    public void CostToMinutes_Seconds_DividesBy60(double costSeconds, double expectedMinutes)
    {
        RoutingCostUnitConverter.CostToMinutes(costSeconds, RoutingCostUnit.Seconds)
            .Should().BeApproximately(expectedMinutes, 1e-9);
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 60.0)]
    [InlineData(0.5, 30.0)]
    public void CostToMinutes_Hours_MultipliesBy60(double costHours, double expectedMinutes)
    {
        RoutingCostUnitConverter.CostToMinutes(costHours, RoutingCostUnit.Hours)
            .Should().BeApproximately(expectedMinutes, 1e-9);
    }

    [Theory]
    [InlineData(10.0, 10.0)]
    public void MinutesToCost_Minutes_IsIdentity(double minutes, double expectedCost)
    {
        RoutingCostUnitConverter.MinutesToCost(minutes, RoutingCostUnit.Minutes)
            .Should().Be(expectedCost);
    }

    [Theory]
    [InlineData(1.0, 60.0)]
    [InlineData(5.0, 300.0)]
    public void MinutesToCost_Seconds_MultipliesBy60(double minutes, double expectedCostSeconds)
    {
        RoutingCostUnitConverter.MinutesToCost(minutes, RoutingCostUnit.Seconds)
            .Should().BeApproximately(expectedCostSeconds, 1e-9);
    }

    [Theory]
    [InlineData(RoutingCostUnit.Minutes, 7.5)]
    [InlineData(RoutingCostUnit.Seconds, 7.5)]
    [InlineData(RoutingCostUnit.Hours, 7.5)]
    public void MinutesToCost_ThenCostToMinutes_RoundTrips(RoutingCostUnit unit, double minutes)
    {
        // The two directions are exact inverses: a request break/cutoff in minutes
        // converted to the cost unit and back must be unchanged, so isochrone breaks,
        // cutoffs, and route time all share one consistent contract.
        var cost = RoutingCostUnitConverter.MinutesToCost(minutes, unit);
        var roundTripped = RoutingCostUnitConverter.CostToMinutes(cost, unit);

        roundTripped.Should().BeApproximately(minutes, 1e-9);
    }

    [Fact]
    public void Seconds_FiveMinuteBreak_MapsTo300CostUnits()
    {
        // A 5-minute isochrone break against a seconds topology must probe pgRouting
        // with a 300-cost cutoff, not 5 — the unit mismatch issue #2008 fixes.
        RoutingCostUnitConverter.MinutesToCost(5.0, RoutingCostUnit.Seconds)
            .Should().Be(300.0);
    }
}
