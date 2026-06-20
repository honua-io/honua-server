// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Pure, database-free solver for the two location-allocation problem types shipped
/// in the first increment of #1874 (minimize-impedance and maximize-coverage) over a
/// precomputed candidate-facility × demand-point impedance matrix. A greedy
/// (interchange-free) heuristic is used: it is exact for a single facility and a
/// good, deterministic approximation for the p-median / maximal-coverage problems
/// without an external LP/heuristic solver. Kept separate from the provider so it can
/// be unit-tested without a pgRouting topology.
/// </summary>
internal static class LocationAllocationSolver
{
    /// <summary>
    /// Solves the location-allocation problem for the supplied request and impedance
    /// matrix.
    /// </summary>
    /// <param name="request">The location-allocation request.</param>
    /// <param name="matrix">
    /// Impedance matrix: <c>matrix[facility][demand]</c> is the network cost (minutes)
    /// from candidate facility to demand point, or
    /// <see cref="double.PositiveInfinity"/> when unreachable.
    /// </param>
    /// <returns>The chosen facilities and per-demand allocations.</returns>
    public static LocationAllocationSolveResult Solve(
        LocationAllocationSolveRequest request,
        double[][] matrix)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(matrix);

        var facilityCount = request.Facilities.Count;
        var demandCount = request.DemandPoints.Count;
        var toFind = Math.Clamp(request.FacilitiesToFind, 1, Math.Max(1, facilityCount));
        var cutoff = request.ImpedanceCutoff;

        // Effective cost for a (facility, demand) pair after applying the cutoff:
        // pairs beyond the cutoff are treated as unreachable (Infinity).
        double Effective(int facility, int demand)
        {
            var c = matrix[facility][demand];
            if (double.IsNaN(c) || double.IsInfinity(c))
            {
                return double.PositiveInfinity;
            }

            return cutoff is { } cut && c > cut ? double.PositiveInfinity : c;
        }

        var chosen = new List<int>();
        // best[demand] = current best effective cost to any already-chosen facility.
        var best = new double[demandCount];
        Array.Fill(best, double.PositiveInfinity);

        for (var pick = 0; pick < toFind; pick++)
        {
            var bestFacility = -1;
            var bestObjectiveDelta = request.ProblemType == LocationAllocationProblemType.MaximizeCoverage
                ? double.NegativeInfinity // maximize covered weight gained
                : double.NegativeInfinity; // maximize weighted-impedance reduction

            for (var f = 0; f < facilityCount; f++)
            {
                if (chosen.Contains(f))
                {
                    continue;
                }

                double delta = 0;
                for (var d = 0; d < demandCount; d++)
                {
                    var weight = request.DemandPoints[d].Weight;
                    var candidateCost = Effective(f, d);

                    if (request.ProblemType == LocationAllocationProblemType.MaximizeCoverage)
                    {
                        // Gain weight only for demand newly brought within the cutoff.
                        var alreadyCovered = !double.IsInfinity(best[d]);
                        if (!alreadyCovered && !double.IsInfinity(candidateCost))
                        {
                            delta += weight;
                        }
                    }
                    else
                    {
                        // Reduction in weighted impedance if this facility becomes the
                        // demand point's closest. Infinity costs contribute nothing.
                        if (double.IsInfinity(candidateCost))
                        {
                            continue;
                        }

                        var current = double.IsInfinity(best[d]) ? double.PositiveInfinity : best[d];
                        if (candidateCost < current)
                        {
                            var currentContribution = double.IsInfinity(current) ? 0 : current * weight;
                            var newContribution = candidateCost * weight;
                            // A switch from "unallocated" to allocated reduces the
                            // unmet-demand objective; model that as positive delta of
                            // the impedance now served (lower is better).
                            delta += double.IsInfinity(current)
                                ? -newContribution + LargeUnservedPenalty(weight)
                                : currentContribution - newContribution;
                        }
                    }
                }

                if (delta > bestObjectiveDelta)
                {
                    bestObjectiveDelta = delta;
                    bestFacility = f;
                }
            }

            if (bestFacility < 0)
            {
                break;
            }

            chosen.Add(bestFacility);

            // Update best[] with the newly chosen facility.
            for (var d = 0; d < demandCount; d++)
            {
                var candidateCost = Effective(bestFacility, d);
                if (candidateCost < best[d])
                {
                    best[d] = candidateCost;
                }
            }
        }

        chosen.Sort();

        // Materialize allocations from the final chosen set.
        var allocations = new List<DemandAllocation>(demandCount);
        double totalWeightedImpedance = 0;
        double totalWeightCovered = 0;
        for (var d = 0; d < demandCount; d++)
        {
            var weight = request.DemandPoints[d].Weight;
            var bestFacility = -1;
            var bestCost = double.PositiveInfinity;
            foreach (var f in chosen)
            {
                var c = Effective(f, d);
                if (c < bestCost)
                {
                    bestCost = c;
                    bestFacility = f;
                }
            }

            if (bestFacility >= 0 && !double.IsInfinity(bestCost))
            {
                allocations.Add(new DemandAllocation(d, bestFacility, weight, bestCost));
                totalWeightedImpedance += bestCost * weight;
                totalWeightCovered += weight;
            }
            else
            {
                allocations.Add(new DemandAllocation(d, -1, weight, double.PositiveInfinity));
            }
        }

        return new LocationAllocationSolveResult(
            chosen,
            allocations,
            totalWeightedImpedance,
            totalWeightCovered);
    }

    // A constant nudge that makes the minimize-impedance greedy prefer allocating
    // previously-unserved demand (large objective gain) over marginally improving
    // already-served demand. Scaled by weight so heavier demand is prioritized.
    private static double LargeUnservedPenalty(double weight) => 1_000_000.0 * weight;
}
