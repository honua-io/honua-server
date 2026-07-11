// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Routing.Features.Routing.Domain;

namespace Honua.Routing.Features.Routing.Providers;

/// <summary>
/// Pure, database-free solver for bounded location-allocation objectives over a
/// precomputed candidate-facility × demand-point impedance matrix. The
/// interchange-free greedy heuristic is exact for one-facility requests. For
/// minimize-facilities it is the standard deterministic greedy set-cover
/// approximation (at most H(n) times the optimal facility count for n coverable
/// demand points). Runtime is O(F²D) and memory is O(D), bounded by the routing
/// facility/demand caps. Kept separate from the provider so it can be unit-tested
/// without a pgRouting topology or heavyweight optimizer dependency.
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
    /// <param name="cancellationToken">Cancellation checked throughout the bounded search.</param>
    /// <returns>The chosen facilities and per-demand allocations.</returns>
    public static LocationAllocationSolveResult Solve(
        LocationAllocationSolveRequest request,
        double[][] matrix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(matrix);
        cancellationToken.ThrowIfCancellationRequested();

        var facilityCount = request.Facilities.Count;
        var demandCount = request.DemandPoints.Count;
        var toFind = request.ProblemType == LocationAllocationProblemType.MinimizeFacilities
            ? facilityCount
            : Math.Clamp(request.FacilitiesToFind, 1, Math.Max(1, facilityCount));
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
            cancellationToken.ThrowIfCancellationRequested();
            var bestFacility = -1;
            var bestObjectiveDelta = double.NegativeInfinity;

            for (var f = 0; f < facilityCount; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();
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
                    else if (request.ProblemType == LocationAllocationProblemType.MinimizeFacilities)
                    {
                        // Greedy set cover: select the candidate that reaches the
                        // greatest number of demand points not yet covered. Demand
                        // weights do not alter the all-demand coverage contract.
                        if (double.IsInfinity(best[d]) && !double.IsInfinity(candidateCost))
                        {
                            delta++;
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

            if (bestFacility < 0 ||
                (request.ProblemType == LocationAllocationProblemType.MinimizeFacilities &&
                 bestObjectiveDelta <= 0))
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
