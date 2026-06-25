// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

/// <summary>
/// A tile bounding box expressed in the coverage's storage CRS, used to resolve
/// a Zarr grid-index window for tile rendering.
/// </summary>
/// <param name="MinX">Western edge in storage-CRS units.</param>
/// <param name="MinY">Southern edge in storage-CRS units.</param>
/// <param name="MaxX">Eastern edge in storage-CRS units.</param>
/// <param name="MaxY">Northern edge in storage-CRS units.</param>
public readonly record struct ZarrTileBounds(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>
/// A resolved plan for rendering one Zarr coverage slice to a map tile: the bounded
/// subset request plus the indices of the X and Y dimensions within the subset shape
/// so the renderer can iterate the 2D grid in row-major order.
/// </summary>
/// <param name="Plan">Bounded subset plan over the target variable.</param>
/// <param name="YDimensionIndex">Index of the Y (row) dimension in the array shape.</param>
/// <param name="XDimensionIndex">Index of the X (column) dimension in the array shape.</param>
/// <param name="GridXMin">Storage-CRS X coordinate of the western edge of the selected window.</param>
/// <param name="GridYMax">Storage-CRS Y coordinate of the northern edge of the selected window.</param>
/// <param name="CellWidth">Storage-CRS width of one grid cell along X.</param>
/// <param name="CellHeight">Storage-CRS height of one grid cell along Y (positive magnitude).</param>
public sealed record ZarrTileSlicePlan(
    ZarrCoverageSubsetPlan Plan,
    int YDimensionIndex,
    int XDimensionIndex,
    double GridXMin,
    double GridYMax,
    double CellWidth,
    double CellHeight);

/// <summary>
/// Resolves a tile request (bbox in storage CRS plus optional time / vertical /
/// extra-dimension index selections) into a bounded 2D Zarr subset suitable for
/// rasterizing to a map tile. Shared, pure, and AOT-safe so the tile adapter never
/// builds an independent Zarr index path. Builds on the coordinate/time-axis
/// resolution introduced for OGC API Coverages (#1790) rather than duplicating it.
/// </summary>
public static class ZarrTileSlicePlanner
{
    /// <summary>
    /// Builds a tile slice plan for one variable of a georeferenced Zarr store.
    /// </summary>
    /// <param name="metadata">Scanned store metadata. Must declare a CRS and extent.</param>
    /// <param name="variable">Variable name, or null to use the store's primary variable.</param>
    /// <param name="bounds">Tile bounds in the store's storage CRS.</param>
    /// <param name="datetime">Optional requested instant/interval for the time axis (resolved to a single index when present).</param>
    /// <param name="verticalIndex">Optional zero-based index on the vertical/elevation axis. Defaults to 0 when the array has a non-spatial, non-temporal axis.</param>
    /// <param name="maxOutputBytes">Upper bound for the decoded subset payload.</param>
    /// <param name="plan">Resolved plan when the method returns true.</param>
    /// <param name="error">Client-safe error when the method returns false.</param>
    /// <returns>True when the tile intersects the coverage and the slice resolves within limits.</returns>
    public static bool TryPlan(
        ZarrStoreMetadata metadata,
        string? variable,
        ZarrTileBounds bounds,
        (DateTimeOffset? Start, DateTimeOffset? End)? datetime,
        int? verticalIndex,
        long maxOutputBytes,
        out ZarrTileSlicePlan? plan,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxOutputBytes);

        plan = null;
        error = null;

        if (metadata.Srid <= 0)
        {
            error = "The coverage is not georeferenced; tile rendering requires a declared CRS and extent.";
            return false;
        }

        var array = ResolveArray(metadata, variable, out error);
        if (array is null)
        {
            return false;
        }

        var xDim = FindSpatialDimension(metadata, array, isX: true);
        var yDim = FindSpatialDimension(metadata, array, isX: false);
        if (xDim < 0 || yDim < 0)
        {
            error = "The coverage variable does not declare resolvable X and Y dimensions for tile rendering.";
            return false;
        }

        var extent = metadata.Extent;
        var width = array.Shape[xDim];
        var height = array.Shape[yDim];
        if (width <= 0 || height <= 0 || extent.XMax <= extent.XMin || extent.YMax <= extent.YMin)
        {
            error = "The coverage extent or grid dimensions are degenerate; tile rendering is unavailable.";
            return false;
        }

        var cellWidth = (extent.XMax - extent.XMin) / width;
        var cellHeight = (extent.YMax - extent.YMin) / height;

        // Map the tile bbox to a half-open grid-index window on each spatial axis.
        // Row 0 is the northernmost row (north-up convention).
        var xStart = (int)Math.Floor((bounds.MinX - extent.XMin) / cellWidth);
        var xStop = (int)Math.Ceiling((bounds.MaxX - extent.XMin) / cellWidth);
        var yStart = (int)Math.Floor((extent.YMax - bounds.MaxY) / cellHeight);
        var yStop = (int)Math.Ceiling((extent.YMax - bounds.MinY) / cellHeight);

        xStart = Math.Clamp(xStart, 0, width);
        xStop = Math.Clamp(xStop, 0, width);
        yStart = Math.Clamp(yStart, 0, height);
        yStop = Math.Clamp(yStop, 0, height);

        if (xStop <= xStart || yStop <= yStart)
        {
            error = "The requested tile does not intersect the coverage extent.";
            return false;
        }

        var subsets = new List<ZarrCoverageDimensionSubset>(array.Shape.Length)
        {
            new(array.DimensionNames[xDim], xStart, xStop),
            new(array.DimensionNames[yDim], yStart, yStop),
        };

        // Resolve every remaining (non-spatial) axis to a single index. The time axis
        // honours the datetime request via the shared CF indexer; other axes use the
        // supplied vertical index (default 0).
        for (var i = 0; i < array.Shape.Length; i++)
        {
            if (i == xDim || i == yDim)
            {
                continue;
            }

            var name = array.DimensionNames[i];
            var isTime = metadata.TemporalDimension is { } t &&
                string.Equals(t, name, StringComparison.OrdinalIgnoreCase);

            if (isTime)
            {
                if (!TryResolveTimeIndex(metadata, array.Shape[i], datetime, out var timeIndex, out error))
                {
                    return false;
                }
                subsets.Add(new ZarrCoverageDimensionSubset(name, timeIndex, timeIndex + 1));
                continue;
            }

            var index = verticalIndex ?? 0;
            if (index < 0 || index >= array.Shape[i])
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The requested index {index} on dimension '{name}' is outside the axis range [0, {array.Shape[i] - 1}].");
                return false;
            }
            subsets.Add(new ZarrCoverageDimensionSubset(name, index, index + 1));
        }

        if (!ZarrCoverageSubsetPlanner.TryPlan(metadata, array.Name, subsets, maxOutputBytes, out var subsetPlan, out error))
        {
            return false;
        }

        var request = subsetPlan!.Request;
        var gridXMin = extent.XMin + (request.Start[xDim] * cellWidth);
        var gridYMax = extent.YMax - (request.Start[yDim] * cellHeight);

        plan = new ZarrTileSlicePlan(
            subsetPlan,
            YDimensionIndex: yDim,
            XDimensionIndex: xDim,
            GridXMin: gridXMin,
            GridYMax: gridYMax,
            CellWidth: cellWidth,
            CellHeight: cellHeight);
        return true;
    }

    private static ZarrArrayMetadata? ResolveArray(ZarrStoreMetadata metadata, string? variable, out string? error)
    {
        error = null;
        if (metadata.Arrays.Length == 0)
        {
            error = "The Zarr store does not expose any variables.";
            return null;
        }

        var resolved = string.IsNullOrWhiteSpace(variable)
            ? metadata.PrimaryVariable ?? metadata.Arrays[0].Name
            : variable;

        foreach (var candidate in metadata.Arrays)
        {
            if (string.Equals(candidate.Name, resolved, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        error = $"Variable '{resolved}' is not available in the coverage.";
        return null;
    }

    private static int FindSpatialDimension(ZarrStoreMetadata metadata, ZarrArrayMetadata array, bool isX)
    {
        var declared = isX ? metadata.SpatialXDimension : metadata.SpatialYDimension;
        for (var i = 0; i < array.DimensionNames.Length; i++)
        {
            var name = array.DimensionNames[i];
            if (declared is not null)
            {
                if (string.Equals(declared, name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
                continue;
            }

            var lower = name.ToLowerInvariant();
            var matches = isX
                ? lower is "x" or "lon" or "longitude"
                : lower is "y" or "lat" or "latitude";
            if (matches)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool TryResolveTimeIndex(
        ZarrStoreMetadata metadata,
        int axisLength,
        (DateTimeOffset? Start, DateTimeOffset? End)? datetime,
        out int index,
        out string? error)
    {
        index = 0;
        error = null;

        // No datetime requested: default to the first (index 0) sample.
        if (datetime is not { } window || (window.Start is null && window.End is null))
        {
            return true;
        }

        if (metadata.Temporal is not { } temporal)
        {
            error = "The coverage does not declare a resolvable time axis for the requested datetime.";
            return false;
        }

        if (!CfTimeAxisIndexer.TryResolveTimeIndexRange(
                temporal.Start,
                temporal.End,
                temporal.StepCount,
                window.Start,
                window.End,
                out var low,
                out var high,
                out error))
        {
            return false;
        }

        // A tile renders a single slice: take the low end of the resolved range,
        // clamped to the axis.
        index = Math.Clamp(low, 0, Math.Max(0, axisLength - 1));
        _ = high;
        return true;
    }
}
