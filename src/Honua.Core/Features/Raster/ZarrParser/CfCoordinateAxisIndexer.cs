// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// Maps numeric coordinate-value requests onto integer indices of a non-spatial,
/// non-temporal Zarr axis (vertical/elevation/pressure-level or any named axis).
/// The vertical/generic analogue of <see cref="CfTimeAxisIndexer"/>: pure and
/// AOT-safe with no allocation beyond an optional error string and no dependency
/// on the read pipeline.
/// </summary>
/// <remarks>
/// Two axis descriptions are supported:
/// <list type="bullet">
/// <item><description><b>Evenly spaced</b> — described by first (<c>start</c>) and last
/// (<c>end</c>) sample coordinate plus <c>count</c>; spacing is
/// <c>(end - start) / (count - 1)</c>. Works for ascending or descending axes
/// (e.g. pressure levels that decrease with height).</description></item>
/// <item><description><b>Explicit coordinate array</b> — an arbitrary, possibly
/// irregular monotonic list of sample coordinates; the nearest sample is selected
/// for an instant and the inclusive covered span for a range.</description></item>
/// </list>
/// An instant request rounds to the nearest sample and is rejected when it falls
/// outside the axis by more than half the local spacing. A range request resolves
/// to the inclusive set of samples whose coordinate lies within the requested
/// (possibly open-ended) interval and is rejected when that set is empty.
/// </remarks>
public static class CfCoordinateAxisIndexer
{
    /// <summary>
    /// Resolves the inclusive index range on a coordinate axis described by an
    /// optional explicit coordinate array or an evenly-spaced descriptor.
    /// </summary>
    /// <param name="axis">Axis metadata to resolve against.</param>
    /// <param name="reqLow">Requested interval low coordinate, or null for an open-ended (<c>../v1</c>) request.</param>
    /// <param name="reqHigh">Requested interval high coordinate, or null for an open-ended (<c>v0/..</c>) request.</param>
    /// <param name="lowInclusive">Resolved inclusive low index when the method returns true.</param>
    /// <param name="highInclusive">Resolved inclusive high index when the method returns true.</param>
    /// <param name="error">Client-safe error when the method returns false.</param>
    /// <returns>True when the request resolves to a non-empty in-range index range.</returns>
    public static bool TryResolveIndexRange(
        ZarrAxis axis,
        double? reqLow,
        double? reqHigh,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(axis);
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        if (axis.Count < 1)
        {
            error = $"The coverage axis '{axis.Name}' declares no samples; subsetting is unavailable.";
            return false;
        }

        if (axis.Count > int.MaxValue)
        {
            error = $"The coverage axis '{axis.Name}' is too large for subsetting.";
            return false;
        }

        if (reqLow is { } rl && reqHigh is { } rh && rl > rh)
        {
            error = $"Subset bounds for axis '{axis.Name}' must satisfy low <= high.";
            return false;
        }

        return axis.Coordinates is { Length: > 0 } coords
            ? ResolveExplicit(axis, coords, reqLow, reqHigh, out lowInclusive, out highInclusive, out error)
            : ResolveEvenlySpaced(axis, reqLow, reqHigh, out lowInclusive, out highInclusive, out error);
    }

    private static bool ResolveEvenlySpaced(
        ZarrAxis axis,
        double? reqLow,
        double? reqHigh,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        var lastIndex = (int)(axis.Count - 1);

        // Single-sample axis: index 0 is the only addressable sample, at `start`.
        if (axis.Count == 1)
        {
            var lowerOk = reqLow is not { } lo || lo <= axis.Start;
            var upperOk = reqHigh is not { } hi || hi >= axis.Start;
            if ((reqLow is null && reqHigh is null) || (lowerOk && upperOk) || IsInstant(reqLow, reqHigh))
            {
                // An instant that lands on the only sample (within tolerance) is accepted below;
                // otherwise the bracket test above governs.
                if (IsInstant(reqLow, reqHigh) && reqLow is { } instant && Math.Abs(instant - axis.Start) > Tolerance(Math.Abs(axis.Start)))
                {
                    error = OutsideError(axis, axis.Start, axis.Start);
                    return false;
                }
                return true;
            }

            error = OutsideError(axis, axis.Start, axis.Start);
            return false;
        }

        var step = (axis.End - axis.Start) / (axis.Count - 1);
        if (step == 0 || double.IsNaN(step) || double.IsInfinity(step))
        {
            error = $"The coverage axis '{axis.Name}' is not evenly spaced or has unknown spacing; subsetting is unavailable.";
            return false;
        }

        var ascending = step > 0;
        var min = ascending ? axis.Start : axis.End;
        var max = ascending ? axis.End : axis.Start;
        var absStep = Math.Abs(step);

        // Instant request: round to the nearest index along the axis direction.
        if (IsInstant(reqLow, reqHigh) && reqLow is { } v)
        {
            var halfStep = absStep / 2;
            if (v < min - halfStep - Tolerance(Math.Abs(min)) || v > max + halfStep + Tolerance(Math.Abs(max)))
            {
                error = OutsideError(axis, min, max);
                return false;
            }

            var idx = (int)Math.Round((v - axis.Start) / step, MidpointRounding.AwayFromZero);
            idx = Math.Clamp(idx, 0, lastIndex);
            lowInclusive = idx;
            highInclusive = idx;
            return true;
        }

        // Interval request. Map the requested coordinate bounds onto fractional
        // indices, then take ceil(low)/floor(high) in axis-index space.
        double lowIndexFractional = 0;
        if (reqLow is { } reqLowVal)
        {
            lowIndexFractional = (reqLowVal - axis.Start) / step;
        }

        double highIndexFractional = lastIndex;
        if (reqHigh is { } reqHighVal)
        {
            highIndexFractional = (reqHighVal - axis.Start) / step;
        }

        // For a descending axis, increasing coordinate maps to decreasing index, so
        // the requested low coordinate corresponds to the high index and vice versa.
        var loIdx = ascending ? lowIndexFractional : highIndexFractional;
        var hiIdx = ascending ? highIndexFractional : lowIndexFractional;

        var low = (int)Math.Ceiling(loIdx - Epsilon);
        var high = (int)Math.Floor(hiIdx + Epsilon);

        if (high < 0 || low > lastIndex || high < low)
        {
            error = $"The requested subset on axis '{axis.Name}' does not intersect the coverage axis [{Fmt(min)}, {Fmt(max)}].";
            return false;
        }

        lowInclusive = Math.Clamp(low, 0, lastIndex);
        highInclusive = Math.Clamp(high, 0, lastIndex);
        return true;
    }

    private static bool ResolveExplicit(
        ZarrAxis axis,
        double[] coords,
        double? reqLow,
        double? reqHigh,
        out int lowInclusive,
        out int highInclusive,
        out string? error)
    {
        lowInclusive = 0;
        highInclusive = 0;
        error = null;

        if (coords.Length != axis.Count)
        {
            error = $"The coverage axis '{axis.Name}' has an inconsistent coordinate array; subsetting is unavailable.";
            return false;
        }

        // Determine monotonic direction. A non-monotonic coordinate array is rejected
        // so that range subsetting is unambiguous.
        var ascending = true;
        var descending = true;
        for (var i = 1; i < coords.Length; i++)
        {
            if (coords[i] <= coords[i - 1])
            {
                ascending = false;
            }
            if (coords[i] >= coords[i - 1])
            {
                descending = false;
            }
        }

        if (coords.Length > 1 && !ascending && !descending)
        {
            error = $"The coverage axis '{axis.Name}' coordinate values are not monotonic; subsetting is unavailable.";
            return false;
        }

        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var c in coords)
        {
            if (c < min) min = c;
            if (c > max) max = c;
        }

        // Instant request: nearest sample within half the local spacing.
        if (IsInstant(reqLow, reqHigh) && reqLow is { } v)
        {
            var nearest = 0;
            var bestDist = double.PositiveInfinity;
            for (var i = 0; i < coords.Length; i++)
            {
                var d = Math.Abs(coords[i] - v);
                if (d < bestDist)
                {
                    bestDist = d;
                    nearest = i;
                }
            }

            var localStep = coords.Length > 1 ? NearestLocalSpacing(coords, nearest) : Tolerance(Math.Abs(coords[0]));
            if (bestDist > (localStep / 2) + Tolerance(Math.Abs(coords[nearest])))
            {
                error = OutsideError(axis, min, max);
                return false;
            }

            lowInclusive = nearest;
            highInclusive = nearest;
            return true;
        }

        // Interval request: every sample whose coordinate lies within the requested span.
        var lo = reqLow ?? double.NegativeInfinity;
        var hi = reqHigh ?? double.PositiveInfinity;
        var tol = Tolerance(Math.Max(Math.Abs(min), Math.Abs(max)));

        var first = -1;
        var last = -1;
        for (var i = 0; i < coords.Length; i++)
        {
            if (coords[i] >= lo - tol && coords[i] <= hi + tol)
            {
                if (first < 0)
                {
                    first = i;
                }
                last = i;
            }
        }

        if (first < 0)
        {
            error = $"The requested subset on axis '{axis.Name}' does not intersect the coverage axis [{Fmt(min)}, {Fmt(max)}].";
            return false;
        }

        // Indices are contiguous because the coordinate array is monotonic.
        lowInclusive = first;
        highInclusive = last;
        return true;
    }

    private static double NearestLocalSpacing(double[] coords, int index)
    {
        double? left = index > 0 ? Math.Abs(coords[index] - coords[index - 1]) : null;
        double? right = index < coords.Length - 1 ? Math.Abs(coords[index + 1] - coords[index]) : null;
        if (left is { } l && right is { } r)
        {
            return Math.Min(l, r);
        }
        return left ?? right ?? 0;
    }

    private static bool IsInstant(double? low, double? high)
        => low is { } l && high is { } h && l == h;

    private static double Tolerance(double magnitude)
        => Math.Max(1e-9, magnitude * 1e-9);

    private const double Epsilon = 1e-9;

    private static string OutsideError(ZarrAxis axis, double min, double max)
        => $"The requested coordinate is outside the coverage axis '{axis.Name}' [{Fmt(min)}, {Fmt(max)}].";

    private static string Fmt(double value)
        => value.ToString("G", CultureInfo.InvariantCulture);
}
