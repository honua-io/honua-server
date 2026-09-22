// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Microsoft.Extensions.Primitives;

namespace Honua.Protocols.Ogc.Api.Coverages.Handlers;

/// <summary>Maps named two-dimensional coverage trims to a canonical raster clip.</summary>
internal static class OgcCoverageSpatialSubset
{
    internal readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY, int Srid);

    internal static bool TryParse(StringValues values, RasterInfo raster, int storageSrid, out Bounds bounds, out string error)
    {
        bounds = default;
        error = "subset must contain finite spatial trims such as Lon(west:east),Lat(south:north) or x(min:max),y(min:max).";
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
                if (limits.Length != 2 ||
                    !double.TryParse(limits[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var low) ||
                    !double.TryParse(limits[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var high) ||
                    !double.IsFinite(low) || !double.IsFinite(high) || low >= high ||
                    (isX ? x.HasValue : y.HasValue))
                {
                    return false;
                }

                if (isGeographic || storageSrid == 4326)
                {
                    var limit = isX ? 180 : 90;
                    if (low < -limit || high > limit)
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

        bounds = new Bounds(x.Value.Low, y.Value.Low, x.Value.High, y.Value.High, geographicNames.Value ? 4326 : storageSrid);
        error = string.Empty;
        return true;
    }
}
