// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Maps OGC API <c>datetime</c> requests onto integer indices of an evenly
/// spaced CF time axis. Pure and AOT-safe: no allocation beyond the optional
/// error string and no dependency on the read pipeline. The axis is described
/// by its first and last sample instants plus the number of samples
/// (<c>stepCount</c>); the per-step spacing is derived as
/// <c>(end - start) / (stepCount - 1)</c> for <c>stepCount &gt; 1</c>.
/// Irregular or unknown spacing is rejected with a client-safe error.
/// </summary>
public static class CfTimeAxisIndexer
{
    /// <summary>
    /// Resolves the inclusive index range on an evenly spaced time axis that an
    /// OGC <c>datetime</c> request selects.
    /// </summary>
    /// <param name="start">First sample instant of the axis.</param>
    /// <param name="end">Last sample instant of the axis.</param>
    /// <param name="stepCount">Number of samples along the axis.</param>
    /// <param name="reqStart">Requested interval start, or null for an open-ended (<c>../t1</c>) request.</param>
    /// <param name="reqEnd">Requested interval end, or null for an open-ended (<c>t0/..</c>) request.</param>
    /// <param name="lowInclusive">Resolved inclusive low index when the method returns true.</param>
    /// <param name="highInclusive">Resolved inclusive high index when the method returns true.</param>
    /// <param name="error">Client-safe error when the method returns false.</param>
    /// <returns>True when the request resolves to a non-empty in-range index range.</returns>
    /// <remarks>
    /// An instant request (<paramref name="reqStart"/> == <paramref name="reqEnd"/>) rounds to the
    /// nearest index and is rejected when it falls outside <c>[start - step/2, end + step/2]</c>.
    /// An interval request takes <c>low = ceil((t0 - start) / step)</c> and
    /// <c>high = floor((t1 - start) / step)</c>, each clamped to the axis, and is rejected when the
    /// clamped range is empty. The axis must declare at least one sample and (for
    /// <c>stepCount &gt; 1</c>) a positive spacing; both are otherwise rejected as irregular/unknown.
    /// </remarks>
    public static bool TryResolveTimeIndexRange(
        DateTimeOffset start,
        DateTimeOffset end,
        long stepCount,
        DateTimeOffset? reqStart,
        DateTimeOffset? reqEnd,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        if (stepCount < 1)
        {
            error = "The coverage time axis declares no samples; temporal subsetting is unavailable.";
            return false;
        }

        if (stepCount > int.MaxValue)
        {
            error = "The coverage time axis is too large for temporal subsetting.";
            return false;
        }

        var lastIndex = (int)(stepCount - 1);

        // Single-sample axis: the only addressable index is 0, located at `start`.
        if (stepCount == 1)
        {
            return ResolveSingleSampleAxis(start, reqStart, reqEnd, out lowInclusive, out highInclusive, out error);
        }

        var stepTicks = (end - start).Ticks / (stepCount - 1);
        if (stepTicks <= 0)
        {
            error = "The coverage time axis is not evenly spaced or has unknown spacing; temporal subsetting is unavailable.";
            return false;
        }

        var step = TimeSpan.FromTicks(stepTicks);

        // Instant request: round to the nearest index.
        if (reqStart is { } instantStart && reqEnd is { } instantEnd && instantStart == instantEnd)
        {
            return ResolveInstant(start, end, lastIndex, step, instantStart, out lowInclusive, out highInclusive, out error);
        }

        // Interval request (possibly open-ended on either side). Compute the
        // unclamped index bounds first so a request that lies entirely outside
        // the axis is rejected rather than silently clamped to an endpoint.
        var low = 0;
        if (reqStart is { } t0)
        {
            low = CeilDiv(t0 - start, step);
        }

        var high = lastIndex;
        if (reqEnd is { } t1)
        {
            high = FloorDiv(t1 - start, step);
        }

        if (high < 0 || low > lastIndex || high < low)
        {
            error = "The requested datetime interval does not intersect the coverage time axis.";
            return false;
        }

        lowInclusive = Math.Clamp(low, 0, lastIndex);
        highInclusive = Math.Clamp(high, 0, lastIndex);
        return true;
    }

    private static bool ResolveSingleSampleAxis(
        DateTimeOffset start,
        DateTimeOffset? reqStart,
        DateTimeOffset? reqEnd,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        // Open on both sides, or an interval/instant that brackets the only sample.
        var lowerOk = reqStart is not { } t0 || t0 <= start;
        var upperOk = reqEnd is not { } t1 || t1 >= start;
        if (lowerOk && upperOk)
        {
            return true;
        }

        error = "The requested datetime does not intersect the coverage time axis.";
        return false;
    }

    private static bool ResolveInstant(
        DateTimeOffset start,
        DateTimeOffset end,
        int lastIndex,
        TimeSpan step,
        DateTimeOffset instant,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        var halfStep = TimeSpan.FromTicks(step.Ticks / 2);
        if (instant < start - halfStep || instant > end + halfStep)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"The requested datetime is outside the coverage time axis [{start:O}, {end:O}].");
            return false;
        }

        var index = RoundDiv(instant - start, step);
        index = Math.Clamp(index, 0, lastIndex);
        lowInclusive = index;
        highInclusive = index;
        return true;
    }

    private static int CeilDiv(TimeSpan offset, TimeSpan step)
    {
        var ticks = offset.Ticks;
        var stepTicks = step.Ticks;
        if (ticks <= 0)
        {
            // Floor division toward negative infinity keeps below-axis requests at/under index 0.
            return (int)Math.Max(long.MinValue + 1, FloorDivTicks(ticks, stepTicks));
        }
        return (int)Math.Min(int.MaxValue, (ticks + stepTicks - 1) / stepTicks);
    }

    private static int FloorDiv(TimeSpan offset, TimeSpan step)
    {
        var value = FloorDivTicks(offset.Ticks, step.Ticks);
        return (int)Math.Clamp(value, int.MinValue, int.MaxValue);
    }

    private static int RoundDiv(TimeSpan offset, TimeSpan step)
    {
        var ticks = offset.Ticks;
        var stepTicks = step.Ticks;
        // Round half away from zero on the positive side; below-axis instants floor to 0 via clamp.
        if (ticks <= 0)
        {
            return (int)Math.Clamp(FloorDivTicks(ticks + (stepTicks / 2), stepTicks), int.MinValue, int.MaxValue);
        }
        return (int)Math.Min(int.MaxValue, (ticks + (stepTicks / 2)) / stepTicks);
    }

    private static long FloorDivTicks(long value, long divisor)
    {
        var quotient = value / divisor;
        if ((value % divisor != 0) && ((value < 0) != (divisor < 0)))
        {
            quotient--;
        }
        return quotient;
    }
}
