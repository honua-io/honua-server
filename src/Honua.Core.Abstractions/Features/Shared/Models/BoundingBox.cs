// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

#pragma warning disable CA1716 // Preserve the stable Shared namespace used across existing contracts.
namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Represents a spatial bounding box (extent) with minimum and maximum coordinates
/// </summary>
public readonly record struct BoundingBox
{
    private const double MinLongitude = -180d;
    private const double MaxLongitude = 180d;

    /// <summary>
    /// Minimum X coordinate (westernmost longitude or leftmost easting)
    /// </summary>
    public required double MinX { get; init; }

    /// <summary>
    /// Minimum Y coordinate (southernmost latitude or bottommost northing)
    /// </summary>
    public required double MinY { get; init; }

    /// <summary>
    /// Maximum X coordinate (easternmost longitude or rightmost easting)
    /// </summary>
    public required double MaxX { get; init; }

    /// <summary>
    /// Maximum Y coordinate (northernmost latitude or topmost northing)
    /// </summary>
    public required double MaxY { get; init; }

    /// <summary>
    /// Spatial reference system identifier (SRID/EPSG code)
    /// </summary>
    public int? SpatialReferenceId { get; init; }

    /// <summary>
    /// Creates a bounding box with the specified coordinates
    /// </summary>
    /// <param name="minX">Minimum X coordinate</param>
    /// <param name="minY">Minimum Y coordinate</param>
    /// <param name="maxX">Maximum X coordinate</param>
    /// <param name="maxY">Maximum Y coordinate</param>
    /// <param name="spatialReferenceId">Optional spatial reference system ID</param>
    /// <returns>BoundingBox instance</returns>
    public static BoundingBox Create(double minX, double minY, double maxX, double maxY, int? spatialReferenceId = null)
        => new()
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY,
            SpatialReferenceId = spatialReferenceId
        };

    /// <summary>
    /// Creates a bounding box from an array of coordinates [minX, minY, maxX, maxY]
    /// </summary>
    /// <param name="coordinates">Array of coordinates in order: minX, minY, maxX, maxY</param>
    /// <param name="spatialReferenceId">Optional spatial reference system ID</param>
    /// <returns>BoundingBox instance</returns>
    /// <exception cref="ArgumentException">Thrown when coordinates array doesn't have exactly 4 elements</exception>
    public static BoundingBox FromArray(double[] coordinates, int? spatialReferenceId = null)
    {
        if (coordinates.Length != 4)
            throw new ArgumentException("Coordinates array must contain exactly 4 elements [minX, minY, maxX, maxY]", nameof(coordinates));

        return Create(coordinates[0], coordinates[1], coordinates[2], coordinates[3], spatialReferenceId);
    }

    /// <summary>
    /// Gets the width (difference between MaxX and MinX)
    /// </summary>
    public readonly double Width => IsAntimeridianCrossing
        ? (MaxLongitude - MinX) + (MaxX - MinLongitude)
        : MaxX - MinX;

    /// <summary>
    /// Gets the height (difference between MaxY and MinY)
    /// </summary>
    public readonly double Height => MaxY - MinY;

    /// <summary>
    /// Gets the center X coordinate
    /// </summary>
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

    /// <summary>
    /// Gets the center Y coordinate
    /// </summary>
    public readonly double CenterY => (MinY + MaxY) / 2.0;

    /// <summary>
    /// Gets the area of the bounding box
    /// </summary>
    public readonly double Area => Width * Height;

    /// <summary>
    /// Converts the bounding box to a coordinate array [minX, minY, maxX, maxY]
    /// </summary>
    /// <returns>Array of coordinates</returns>
    public readonly double[] ToArray() => [MinX, MinY, MaxX, MaxY];

    /// <summary>
    /// Checks if this bounding box intersects with another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to test intersection with</param>
    /// <returns>True if the bounding boxes intersect, false otherwise</returns>
    public readonly bool Intersects(BoundingBox other)
    {
        if (MinY > other.MaxY || MaxY < other.MinY)
        {
            return false;
        }

        var ranges = GetLongitudeRanges();
        var otherRanges = other.GetLongitudeRanges();

        foreach (var range in ranges)
        {
            foreach (var otherRange in otherRanges)
            {
                if (range.Min <= otherRange.Max && range.Max >= otherRange.Min)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if this bounding box contains another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to test containment</param>
    /// <returns>True if this bounding box contains the other, false otherwise</returns>
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
            var contained = false;
            foreach (var range in ranges)
            {
                if (range.Min <= otherRange.Min && range.Max >= otherRange.Max)
                {
                    contained = true;
                    break;
                }
            }

            if (!contained)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Expands this bounding box to include another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to include</param>
    /// <returns>New bounding box that encompasses both</returns>
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

    /// <summary>
    /// Gets the intersection of this bounding box with another bounding box
    /// </summary>
    /// <param name="other">Other bounding box to intersect with</param>
    /// <returns>Intersection bounding box, or null if they don't intersect</returns>
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

    /// <summary>
    /// Validates that the bounding box has valid coordinates (MinX ≤ MaxX, MinY ≤ MaxY)
    /// </summary>
    /// <returns>True if the bounding box is valid, false otherwise</returns>
    public readonly bool IsValid => MinY <= MaxY && (MinX <= MaxX || IsAntimeridianCrossing);

    /// <summary>
    /// Indicates whether the bounding box crosses the antimeridian (dateline).
    /// </summary>
    public readonly bool IsAntimeridianCrossing =>
        MinX > MaxX &&
        MinX >= MinLongitude &&
        MinX <= MaxLongitude &&
        MaxX >= MinLongitude &&
        MaxX <= MaxLongitude;

    /// <summary>
    /// Returns a string representation of the bounding box
    /// </summary>
    /// <returns>String in format "BoundingBox[minX, minY, maxX, maxY] SRID:xxxx"</returns>
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

    /// <summary>
    /// Chooses the shortest longitude span that covers the provided ranges.
    /// </summary>
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

    /// <summary>
    /// Normalizes longitude ranges into the [0, 360) domain.
    /// </summary>
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

    /// <summary>
    /// Normalizes a longitude range into the [-180, 180] domain.
    /// </summary>
    internal static (double Min, double Max) NormalizeRangeTo180((double Min, double Max) range)
    {
        var min = NormalizeLongitudeTo180(range.Min);
        var max = NormalizeLongitudeTo180(range.Max);
        return (min, max);
    }

    /// <summary>
    /// Normalizes a longitude into the [0, 360) domain.
    /// </summary>
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

    /// <summary>
    /// Normalizes a longitude into the [-180, 180] domain.
    /// </summary>
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

    /// <summary>
    /// Returns true when <paramref name="srid"/> is a known geographic (lat/lon) CRS.
    /// Only applied when no WKT is available; keeps the check narrow to confirmed
    /// geographic 2D codes so projected CRSes are never mis-classified. A null SRID
    /// is treated as geographic. This is the single source of truth for the geographic
    /// EPSG-code list; <see cref="SpatialReference.IsGeographic"/>'s WKID fallback
    /// delegates here. Every entry must be a confirmed geographic 2D CRS because the
    /// list also drives protocol axis-order decisions (WMS 1.3.0 BBOX, WFS 2.0 CRS,
    /// FES filter geometries).
    /// </summary>
    internal static bool IsGeographicSrid(int? srid)
        => srid is null
           || srid == 4326 || srid == 4979                       // WGS 84
           || srid == 4258                                        // ETRS89
           || srid == 4230                                        // ED50
           || srid == 4267 || srid == 4269                       // NAD27 / NAD83
           || srid == 4277                                        // OSGB 1936
           || srid == 4283 || srid == 7844                       // GDA94 / GDA2020
           || srid == 4490                                        // CGCS2000
           || srid == 4171                                        // RGF93
           || srid == 4167                                        // NZGD2000
           || srid == 6668 || srid == 4612                       // JGD2011 / JGD2000
           || srid == 4674 || srid == 4618                       // SIRGAS2000 / SAD69
           || srid == 4617                                        // NAD83(CSRS)
           || srid == 4275                                        // NTF
           || srid == 4149;                                       // CH1903

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
