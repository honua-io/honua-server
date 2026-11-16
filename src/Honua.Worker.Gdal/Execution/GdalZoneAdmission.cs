// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using NetTopologySuite.Features;

namespace Honua.Worker.Gdal.Execution;

internal static class GdalZoneAdmission
{
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
        foreach (var geometry in (zones).Select(feature => feature.Geometry))
        {
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
