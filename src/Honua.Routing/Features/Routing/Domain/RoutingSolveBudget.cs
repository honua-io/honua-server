// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

/// <summary>
/// Pair-count budget for network solves whose work is the product of two input
/// sets (closest facility, OD cost matrix). NAServer translation and the routing
/// provider both call this so a geoprocessing job cannot bypass the adapter.
/// </summary>
public static class RoutingSolveBudget
{
    /// <summary>
    /// Number of ranked pairs a solve will attempt. A positive target keeps at most
    /// that many partners per left-hand input; otherwise every partner is ranked.
    /// </summary>
    public static int RankedPairCount(int leftCount, int rightCount, int? targetCount)
    {
        if (leftCount <= 0 || rightCount <= 0)
        {
            return 0;
        }

        var ranked = targetCount is > 0
            ? Math.Min(targetCount.Value, rightCount)
            : rightCount;
        return checked(leftCount * ranked);
    }

    /// <summary>
    /// Rejects a solve whose ranked pair count exceeds <paramref name="maxSolvePairs"/>.
    /// </summary>
    public static void Ensure(int pairCount, int maxSolvePairs, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSolvePairs);

        if (pairCount > maxSolvePairs)
        {
            throw new RoutingSolveBudgetExceededException(
                $"{operation} would solve {pairCount} pairs, which exceeds the maximum of {maxSolvePairs}.");
        }
    }
}

/// <summary>
/// A network solve asked for more ranked pairs than <see cref="RoutingConfiguration.MaxSolvePairs"/>.
/// </summary>
/// <param name="message">The error message.</param>
public sealed class RoutingSolveBudgetExceededException(string message) : Exception(message);
