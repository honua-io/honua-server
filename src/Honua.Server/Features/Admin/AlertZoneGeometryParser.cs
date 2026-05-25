// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geometry.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Features.Admin;

internal static class AlertZoneGeometryParser
{
    private static readonly WKBReader _wkbReader = new();
    private static readonly WKBWriter _wkbWriter = new();

    public static bool TryParseWkt(
        string? wkt,
        int srid,
        IGeometryService geometryService,
        out byte[]? wkb,
        out string error)
    {
        if (string.IsNullOrWhiteSpace(wkt))
        {
            wkb = null;
            error = string.Empty;
            return true;
        }

        try
        {
            wkb = geometryService.ConvertWktToWkb(wkt, srid);
            if (wkb is null)
            {
                error = "Invalid WKT geometry.";
                return false;
            }

            Geometry geometry = _wkbReader.Read(wkb);
            geometry.SRID = srid;

            if (geometry is Polygon polygon)
            {
                geometry = new MultiPolygon(new[] { polygon }) { SRID = srid };
            }

            if (geometry is not MultiPolygon)
            {
                wkb = null;
                error = "Zone geometry must be Polygon or MultiPolygon.";
                return false;
            }

            wkb = _wkbWriter.Write(geometry);
            error = string.Empty;
            return true;
        }
        catch (ArgumentException ex)
        {
            wkb = null;
            error = ex.Message;
            return false;
        }
        catch (ParseException)
        {
            wkb = null;
            error = "Invalid WKT geometry.";
            return false;
        }
    }
}
