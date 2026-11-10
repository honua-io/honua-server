// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Raster.ZarrParser;

public static class CfTimeAxisIndexer
{
    // An instant more than half a step outside the axis is rejected, and an interval that clamps to nothing is rejected.
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
