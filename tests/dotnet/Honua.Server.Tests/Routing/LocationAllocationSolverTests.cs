// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Routing.Features.Routing.Providers;
using Xunit;

namespace Honua.Server.Tests.Routing;

/// <summary>
/// Unit tests (no database) for the pure <see cref="LocationAllocationSolver"/> used
/// by the location-allocation solve (#1874). Exercises the minimize-impedance and
/// maximize-coverage objectives over a fixed impedance matrix.
/// </summary>
public sealed class LocationAllocationSolverTests
{
    private static LocationAllocationSolveRequest Request(
        int facilityCount,
        IReadOnlyList<double> demandWeights,
        LocationAllocationProblemType problemType,
        int facilitiesToFind,
        double? cutoff = null)
    {
        var facilities = Enumerable.Range(0, facilityCount).Select(_ => new RoutePoint(0, 0)).ToList();
        var demand = demandWeights.Select(w => new DemandPoint(new RoutePoint(0, 0), w)).ToList();
        return new LocationAllocationSolveRequest(facilities, demand, problemType, facilitiesToFind, cutoff);
    }

    [Fact]
    public void Solve_MinimizeImpedance_PicksFacilityClosestToWeightedDemand()
    {
        // Two candidate facilities, three demand points.
        // matrix[facility][demand]: facility 1 is much closer to the heavy demand.
        var matrix = new[]
        {
            new[] { 10.0, 10.0, 10.0 }, // facility 0
            new[] { 1.0, 1.0, 1.0 },    // facility 1
        };
        var request = Request(2, [5, 5, 5], LocationAllocationProblemType.MinimizeImpedance, facilitiesToFind: 1);

        var result = LocationAllocationSolver.Solve(request, matrix);

        result.ChosenFacilityIds.Should().ContainSingle().Which.Should().Be(1);
        result.Allocations.Should().OnlyContain(a => a.AllocatedFacilityId == 1);
        result.TotalWeightCovered.Should().Be(15);
    }

    [Fact]
    public void Solve_MaximizeCoverage_PicksFacilityCoveringMostWeightWithinCutoff()
    {
        // Facility 0 covers demands 0,1 within the cutoff; facility 1 covers only 2.
        var matrix = new[]
        {
            new[] { 5.0, 5.0, 100.0 }, // facility 0 -> covers d0,d1
            new[] { 100.0, 100.0, 5.0 }, // facility 1 -> covers d2
        };
        var request = Request(2, [3, 3, 1], LocationAllocationProblemType.MaximizeCoverage, facilitiesToFind: 1, cutoff: 10);

        var result = LocationAllocationSolver.Solve(request, matrix);

        // Facility 0 covers weight 6 vs facility 1's weight 1, so it is chosen.
        result.ChosenFacilityIds.Should().ContainSingle().Which.Should().Be(0);
        result.TotalWeightCovered.Should().Be(6);
    }

    [Fact]
    public void Solve_RespectsCutoff_LeavesUnreachableDemandUnallocated()
    {
        var matrix = new[]
        {
            new[] { 5.0, 50.0 }, // demand 1 is beyond the cutoff
        };
        var request = Request(1, [1, 1], LocationAllocationProblemType.MinimizeImpedance, facilitiesToFind: 1, cutoff: 10);

        var result = LocationAllocationSolver.Solve(request, matrix);

        result.Allocations[0].AllocatedFacilityId.Should().Be(0);
        result.Allocations[1].AllocatedFacilityId.Should().Be(-1);
        result.TotalWeightCovered.Should().Be(1);
    }
}
