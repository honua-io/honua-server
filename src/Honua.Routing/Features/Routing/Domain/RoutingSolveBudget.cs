// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Routing.Features.Routing.Domain;

public static class RoutingSolveBudget
{
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

public sealed class RoutingSolveBudgetExceededException(string message) : Exception(message);
