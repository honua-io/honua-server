// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Admission control for the <c>raster.zonal-statistics</c> zone
/// <see cref="FeatureCollection"/>. Each zone drives its own gdalwarp + gdalinfo
/// subprocess pair, so the cumulative per-zone work is unbounded even though a
/// per-invocation timeout guards each individual tool call. This guard rejects a
/// payload whose zone count — or total geometry vertex count — exceeds the
/// configured caps BEFORE the per-zone loop begins (#2766).
/// </summary>
internal static class GdalZoneAdmission
{
    /// <summary>
    /// Rejects the payload when the zone count exceeds
    /// <see cref="GdalWorkerOptions.MaxZoneCount"/> or the cumulative vertex count
    /// across all zone geometries exceeds
    /// <see cref="GdalWorkerOptions.MaxZoneVertices"/>. Returns <c>true</c> (admit)
    /// otherwise.
    /// </summary>
    public static bool TryAdmit(FeatureCollection zones, GdalWorkerOptions options, out string error)
    {
        ArgumentNullException.ThrowIfNull(zones);
        ArgumentNullException.ThrowIfNull(options);
        error = "";

        if (zones.Count > options.MaxZoneCount)
        {
            error = $"'zones' declares {zones.Count.ToString(CultureInfo.InvariantCulture)} features, exceeding configured MaxZoneCount={options.MaxZoneCount.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        // Not a .Select(...).Sum(...) candidate: the running total must short-circuit as
        // soon as MaxZoneVertices is exceeded (the DoS admission guard this type exists
        // for, #2766) rather than first materializing every geometry's vertex count.
        long totalVertices = 0;
        foreach (var feature in zones)
        {
            var geometry = feature.Geometry;
            if (geometry is null)
            {
                continue;
            }

            totalVertices += geometry.NumPoints;
            if (totalVertices > options.MaxZoneVertices)
            {
                error = $"'zones' geometries carry more than the configured MaxZoneVertices={options.MaxZoneVertices.ToString(CultureInfo.InvariantCulture)} cumulative vertices";
                return false;
            }
        }

        return true;
    }
}
