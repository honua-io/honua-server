// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.Ogc.Api.Coverages.Handlers;

internal static class OgcCoverageSpatialSubset
{
    internal readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY, int Srid);

    internal static bool TryParse(StringValues values, RasterInfo raster, int storageSrid, out Bounds bounds, out string error)
    {
        bounds = default;
        error = "subset must contain finite spatial slices or trims such as Lon(west:east),Lat(south:north) or x(min:max),y(min:max).";
        (double Low, double High)? x = null;
        (double Low, double High)? y = null;
        bool? geographicNames = null;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var raw in value.Split(',', StringSplitOptions.TrimEntries))
            {
                var open = raw.IndexOf('(', StringComparison.Ordinal);
                if (open <= 0 || !raw.EndsWith(')'))
                {
                    return false;
                }

                var axis = raw[..open].Trim().ToLowerInvariant();
                var isX = axis is "x" or "lon" or "long" or "longitude";
                if (!isX && axis is not ("y" or "lat" or "latitude"))
                {
                    return false;
                }

                var isGeographic = axis is not ("x" or "y");
                if (geographicNames.HasValue && geographicNames.Value != isGeographic)
                {
                    return false;
                }
                geographicNames = isGeographic;

                var limits = raw[(open + 1)..^1].Split(':', StringSplitOptions.TrimEntries);
                if (limits.Length is not (1 or 2) ||
                    !double.TryParse(limits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var low) ||
                    !double.TryParse(limits[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var high) ||
                    !double.IsFinite(low) || !double.IsFinite(high) ||
                    (limits.Length == 2 && (low.CompareTo(high) == 0 || (low > high && !(isX && (isGeographic || storageSrid == 4326))))) ||
                    (isX ? x.HasValue : y.HasValue))
                {
                    return false;
                }

                if (isGeographic || storageSrid == 4326)
                {
                    var limit = isX ? 180 : 90;
                    if (low < -limit || low > limit || high < -limit || high > limit)
                    {
                        return false;
                    }
                }

                if (isX)
                {
                    x = (low, high);
                }
                else
                {
                    y = (low, high);
                }
            }
        }

        if (!geographicNames.HasValue)
        {
            return false;
        }

        if (geographicNames.Value)
        {
            // An omitted axis is unrestricted; the raster store clips to its data.
            x ??= (-180, 180);
            y ??= (-90, 90);
        }
        else if (raster.Extent is { } extent && extent.Srid.GetValueOrDefault(storageSrid) == storageSrid)
        {
            x ??= (extent.XMin, extent.XMax);
            y ??= (extent.YMin, extent.YMax);
        }

        if (!x.HasValue || !y.HasValue)
        {
            return false;
        }

        var completed = CompleteBounds(new Bounds(x.Value.Low, y.Value.Low, x.Value.High, y.Value.High,
            geographicNames.Value ? 4326 : storageSrid), raster, storageSrid);
        bounds = completed.Bounds;
        error = completed.Error;
        return error.Length == 0;
    }

    private static (Bounds Bounds, string Error) CompleteBounds(Bounds bounds, RasterInfo raster, int storageSrid)
    {
        if (bounds.MinX.CompareTo(bounds.MaxX) != 0 && bounds.MinY.CompareTo(bounds.MaxY) != 0)
        {
            return (bounds, string.Empty);
        }
        // ST_Clip selects pixel centers. Select the native grid cell containing
        // the slice ordinate instead of passing a zero-area geometry to export.
        var expanded = ExpandSlices(bounds, raster, storageSrid);
        return expanded.HasValue
            ? (expanded.Value, string.Empty)
            : (default, "Spatial slices require an axis-aligned native grid and an ordinate within its extent. Use native x/y axes when the storage CRS is projected.");
    }

    private static Bounds? ExpandSlices(Bounds bounds, RasterInfo raster, int storageSrid)
    {
        if (bounds.Srid != storageSrid || raster.Extent is not { } extent ||
            extent.Srid.GetValueOrDefault(storageSrid) != storageSrid || raster.Width <= 0 || raster.Height <= 0 ||
            (raster.GeoTransform is { Length: >= 6 } transform &&
                (transform[2].CompareTo(0) != 0 || transform[4].CompareTo(0) != 0)))
        {
            return null;
        }

        var x = ExpandAxis((bounds.MinX, bounds.MaxX), (extent.XMin, extent.XMax), raster.Width);
        var y = ExpandAxis((bounds.MinY, bounds.MaxY), (extent.YMin, extent.YMax), raster.Height);
        return x.HasValue && y.HasValue
            ? bounds with { MinX = x.Value.Low, MaxX = x.Value.High, MinY = y.Value.Low, MaxY = y.Value.High }
            : null;
    }

    private static (double Low, double High)? ExpandAxis(
        (double Low, double High) limits, (double Low, double High) extent, int size)
    {
        if (limits.Low.CompareTo(limits.High) != 0)
        {
            return limits;
        }
        var step = (extent.High - extent.Low) / size;
        if (!double.IsFinite(step) || step <= 0 || limits.Low < extent.Low || limits.Low > extent.High)
        {
            return null;
        }
        var cell = Math.Min(size - 1, Math.Floor((limits.Low - extent.Low) / step));
        return (extent.Low + cell * step, extent.Low + (cell + 1) * step);
    }
}
