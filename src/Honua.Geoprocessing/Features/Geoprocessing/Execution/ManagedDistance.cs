// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Geoprocessing.Execution;

/// <summary>
/// Overflow-safe predicates for Euclidean distances represented by finite coordinates.
/// </summary>
internal static class ManagedDistance
{
    public static bool IsWithin(double dx, double dy, double distance)
    {
        if (!double.IsFinite(distance) || distance < 0)
        {
            return false;
        }

        if (distance == 0)
        {
            return dx == 0 && dy == 0;
        }

        // Divide before squaring. Both terms are bounded by one after the early check,
        // so coordinates such as +/-1e200 never turn a finite comparison into Infinity <= Infinity.
        var normalizedX = Math.Abs(dx) / distance;
        var normalizedY = Math.Abs(dy) / distance;
        if (!double.IsFinite(normalizedX) || !double.IsFinite(normalizedY)
            || normalizedX > 1 || normalizedY > 1)
        {
            return false;
        }

        return (normalizedX * normalizedX) + (normalizedY * normalizedY) <= 1;
    }
}
