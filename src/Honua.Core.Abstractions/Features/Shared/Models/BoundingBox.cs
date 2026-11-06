// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

public readonly record struct BoundingBox
{
    private const double MinLongitude = -180d;
    private const double MaxLongitude = 180d;

    public required double MinX { get; init; }

    public required double MinY { get; init; }

    public required double MaxX { get; init; }

    public required double MaxY { get; init; }

    public int? SpatialReferenceId { get; init; }

    public static BoundingBox Create(double minX, double minY, double maxX, double maxY, int? spatialReferenceId = null)
        => new()
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            SpatialReferenceId = spatialReferenceId
        };

    public static BoundingBox FromArray(double[] coordinates, int? spatialReferenceId = null)
    {
        if (coordinates.Length != 4)
            throw new ArgumentException("Coordinates array must contain exactly 4 elements [minX, minY, maxX, maxY]", nameof(coordinates));

        return Create(coordinates[0], coordinates[1], coordinates[2], coordinates[3], spatialReferenceId);
    }

    public readonly double Width => IsAntimeridianCrossing
        ? (MaxLongitude - MinX) + (MaxX - MinLongitude)
        : MaxX - MinX;

    public readonly double Height => MaxY - MinY;

    public readonly double CenterX
    {
        get
        {
            if (!IsAntimeridianCrossing)
            {
                return (MinX + MaxX) / 2.0;
            }

            var center = MinX + (Width / 2.0);
            return NormalizeLongitude(center);
        }
    }

    public readonly double CenterY => (MinY + MaxY) / 2.0;

    public readonly double Area => Width * Height;

    public readonly double[] ToArray() => [MinX, MinY, MaxX, MaxY];

    public readonly bool Intersects(BoundingBox other)
    {
        if (MinY > other.MaxY || MaxY < other.MinY)
        {
            return false;
        }

        var ranges = GetLongitudeRanges();
        var otherRanges = other.GetLongitudeRanges();

        var rangeIndex = 0;
        while (rangeIndex < ranges.Count)
        {
            var range = ranges[rangeIndex];
            var otherRangeIndex = 0;
            while (otherRangeIndex < otherRanges.Count)
            {
                var otherRange = otherRanges[otherRangeIndex];
                if (range.Min <= otherRange.Max && range.Max >= otherRange.Min)
                {
                    return true;
                }

                otherRangeIndex++;
            }

            rangeIndex++;
        }

        return false;
    }

    public readonly bool Contains(BoundingBox other)
    {
        if (MinY > other.MinY || MaxY < other.MaxY)
        {
            return false;
        }

        var ranges = GetLongitudeRanges();
        var otherRanges = other.GetLongitudeRanges();

        foreach (var otherRange in otherRanges)
        {
            var contained = ranges.Any(range => range.Min <= otherRange.Min && range.Max >= otherRange.Max);

            if (!contained)
            {
                return false;
            }
        }

        return true;
    }

    public readonly BoundingBox Union(BoundingBox other)
    {
        var srid = SpatialReferenceId ?? other.SpatialReferenceId;
        var minY = Math.Min(MinY, other.MinY);
        var maxY = Math.Max(MaxY, other.MaxY);

        // Only apply antimeridian / degree-domain longitude normalization for geographic
        // (lat/lon) CRSes. Projected CRSes carry metric or other non-degree coordinates
        // where the modulo-360 normalization produces completely wrong results.
        if (!IsGeographicSrid(srid))
        {
            return Create(Math.Min(MinX, other.MinX), minY, Math.Max(MaxX, other.MaxX), maxY, srid);
        }

        var mergedRanges = MergeRanges(GetLongitudeRanges().Concat(other.GetLongitudeRanges()));

        if (mergedRanges.Count == 0)
        {
            return Create(MinX, minY, MaxX, maxY, srid);
        }

        var (minX, maxX) = ResolveMinimalLongitudeSpan(mergedRanges);
        return Create(minX, minY, maxX, maxY, srid);
    }

    public readonly BoundingBox? Intersection(BoundingBox other)
    {
        var srid = SpatialReferenceId ?? other.SpatialReferenceId;
        var minY = Math.Max(MinY, other.MinY);
        var maxY = Math.Min(MaxY, other.MaxY);

        if (minY > maxY)
        {
            return null;
        }

        // Only apply antimeridian / degree-domain longitude normalization for geographic
        // (lat/lon) CRSes. Projected CRSes carry metric or other non-degree coordinates
        // where the modulo-360 normalization produces completely wrong results.
        if (!IsGeographicSrid(srid))
        {
            var projMinX = Math.Max(MinX, other.MinX);
            var projMaxX = Math.Min(MaxX, other.MaxX);
            return projMinX <= projMaxX ? Create(projMinX, minY, projMaxX, maxY, srid) : null;
        }

        var intersections = IntersectRanges(GetLongitudeRanges(), other.GetLongitudeRanges());
        if (intersections.Count == 0)
        {
            return null;
        }

        var (minX, maxX) = ResolveMinimalLongitudeSpan(intersections);
        return Create(minX, minY, maxX, maxY, srid);
    }

    public readonly bool IsValid => MinY <= MaxY && (MinX <= MaxX || IsAntimeridianCrossing);

    public readonly bool IsAntimeridianCrossing =>
        MinX > MaxX &&
        MinX >= MinLongitude &&
        MinX <= MaxLongitude &&
        MaxX >= MinLongitude &&
        MaxX <= MaxLongitude;

    public override readonly string ToString()
        => SpatialReferenceId.HasValue
            ? $"BoundingBox[{MinX}, {MinY}, {MaxX}, {MaxY}] SRID:{SpatialReferenceId}"
            : $"BoundingBox[{MinX}, {MinY}, {MaxX}, {MaxY}]";

    private readonly IReadOnlyList<(double Min, double Max)> GetLongitudeRanges()
    {
        if (IsAntimeridianCrossing)
        {
            return new[]
            {
                (MinX, MaxLongitude),
                (MinLongitude, MaxX)
            };
        }

        return new[] { (MinX, MaxX) };
    }

    private static List<(double Min, double Max)> MergeRanges(IEnumerable<(double Min, double Max)> ranges)
    {
        var ordered = ranges.OrderBy(range => range.Min).ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var merged = new List<(double Min, double Max)> { ordered[0] };
        for (var i = 1; i < ordered.Count; i++)
        {
            var current = ordered[i];
            var last = merged[^1];

            if (current.Min <= last.Max)
            {
                merged[^1] = (last.Min, Math.Max(last.Max, current.Max));
                continue;
            }

            merged.Add(current);
        }

        return merged;
    }

    private static List<(double Min, double Max)> IntersectRanges(
        IReadOnlyList<(double Min, double Max)> ranges,
        IReadOnlyList<(double Min, double Max)> otherRanges)
    {
        var intersections = new List<(double Min, double Max)>();

        foreach (var range in ranges)
        {
            foreach (var otherRange in otherRanges)
            {
                var min = Math.Max(range.Min, otherRange.Min);
                var max = Math.Min(range.Max, otherRange.Max);
                if (min <= max)
                {
                    intersections.Add((min, max));
                }
            }
        }

        return MergeRanges(intersections);
    }

    internal static (double Min, double Max) ResolveMinimalLongitudeSpan(List<(double Min, double Max)> ranges)
    {
        if (ranges.Count == 0)
        {
            return (MinLongitude, MaxLongitude);
        }

        var normalizedRanges = NormalizeRangesTo360(ranges);
        var merged = MergeRanges(normalizedRanges);
        if (merged.Count == 0)
        {
            return (MinLongitude, MaxLongitude);
        }

        var coverage = merged.Sum(range => range.Max - range.Min);
        if (coverage >= 360d - 1e-9)
        {
            return (MinLongitude, MaxLongitude);
        }

        if (merged.Count == 1)
        {
            return NormalizeRangeTo180(merged[0]);
        }

        var largestGap = -1d;
        var gapStart = 0d;
        var gapEnd = 0d;
        for (var i = 0; i < merged.Count; i++)
        {
            var current = merged[i];
            var next = i == merged.Count - 1 ? merged[0] : merged[i + 1];
            var gap = i == merged.Count - 1
                ? (next.Min + 360d) - current.Max
                : next.Min - current.Max;

            if (gap > largestGap)
            {
                largestGap = gap;
                gapStart = current.Max;
                gapEnd = next.Min;
            }
        }

        var spanStart = gapEnd;
        var spanEnd = gapStart;
        return NormalizeRangeTo180((spanStart, spanEnd));
    }

    internal static List<(double Min, double Max)> NormalizeRangesTo360(List<(double Min, double Max)> ranges)
    {
        var normalized = new List<(double Min, double Max)>(ranges.Count * 2);
        foreach (var range in ranges)
        {
            var start = NormalizeLongitudeTo360(range.Min);
            var end = NormalizeLongitudeTo360(range.Max);

            if (start <= end)
            {
                normalized.Add((start, end));
                continue;
            }

            normalized.Add((start, 360d));
            normalized.Add((0d, end));
        }

        return normalized;
    }

    internal static (double Min, double Max) NormalizeRangeTo180((double Min, double Max) range)
    {
        var min = NormalizeLongitudeTo180(range.Min);
        var max = NormalizeLongitudeTo180(range.Max);
        return (min, max);
    }

    internal static double NormalizeLongitudeTo360(double value)
    {
        var normalized = value % 360d;
        if (normalized < 0d)
        {
            normalized += 360d;
        }

        if (normalized >= 360d)
        {
            normalized -= 360d;
        }

        return normalized;
    }

    internal static double NormalizeLongitudeTo180(double value)
    {
        var normalized = value % 360d;
        if (normalized > 180d)
        {
            normalized -= 360d;
        }
        else if (normalized < -180d)
        {
            normalized += 360d;
        }

        return normalized;
    }

    // Without WKT this value type answers from the static allowlist, because it cannot call the registry.
    internal static bool IsGeographicSrid(int? srid)
        => GeographicSridClassifier.IsGeographicSrid(srid);

    private static double NormalizeLongitude(double value)
    {
        if (value > MaxLongitude)
        {
            return value - 360d;
        }

        if (value < MinLongitude)
        {
            return value + 360d;
        }

        return value;
    }
}
#pragma warning restore CA1716
