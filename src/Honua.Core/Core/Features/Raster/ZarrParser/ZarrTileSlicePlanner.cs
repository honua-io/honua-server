// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public readonly record struct ZarrTileBounds(double MinX, double MinY, double MaxX, double MaxY);

public sealed record ZarrTileSlicePlan(
    ZarrCoverageSubsetPlan Plan,
    int YDimensionIndex,
    int XDimensionIndex,
    double GridXMin,
    double GridYMax,
    double CellWidth,
    double CellHeight,
    bool YAxisAscending = false);

public static class ZarrTileSlicePlanner
{
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
        var xStart = (int)Math.Floor((bounds.MinX - extent.XMin) / cellWidth);
        var xStop = (int)Math.Ceiling((bounds.MaxX - extent.XMin) / cellWidth);
        var yStart = metadata.YAxisAscending
            ? (int)Math.Floor((bounds.MinY - extent.YMin) / cellHeight)
            : (int)Math.Floor((extent.YMax - bounds.MaxY) / cellHeight);
        var yStop = metadata.YAxisAscending
            ? (int)Math.Ceiling((bounds.MaxY - extent.YMin) / cellHeight)
            : (int)Math.Ceiling((extent.YMax - bounds.MinY) / cellHeight);

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
        var gridYMax = metadata.YAxisAscending
            ? extent.YMin + (request.Stop[yDim] * cellHeight)
            : extent.YMax - (request.Start[yDim] * cellHeight);

        plan = new ZarrTileSlicePlan(
            subsetPlan,
            YDimensionIndex: yDim,
            XDimensionIndex: xDim,
            GridXMin: gridXMin,
            GridYMax: gridYMax,
            CellWidth: cellWidth,
            CellHeight: cellHeight,
            YAxisAscending: metadata.YAxisAscending);
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

        var match = metadata.Arrays.FirstOrDefault(candidate => string.Equals(candidate.Name, resolved, StringComparison.Ordinal));
        if (match is not null)
        {
            return match;
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
