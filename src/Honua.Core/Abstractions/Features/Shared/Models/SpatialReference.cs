// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public readonly record struct SpatialReference
{
    public required int Wkid { get; init; }

    public int? LatestWkid { get; init; }

    public int? VcsWkid { get; init; }

    public int? LatestVcsWkid { get; init; }

    public string? Wkt { get; init; }

    public int Srid => Wkid;

    public string? WellKnownText => Wkt;

    public static readonly SpatialReference WGS84 = new()
    {
        Wkid = 4326,
        Wkt = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]"
    };

    public static readonly SpatialReference WebMercator = new()
    {
        Wkid = 3857,
        Wkt = "PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Mercator_1SP\"],PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1],PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1]]"
    };

    public readonly string DisplayName => Wkid switch
    {
        4326 => "WGS 84 (Geographic)",
        3857 => "WGS 84 / Web Mercator",
        _ => $"EPSG:{Wkid}"
    };

    public readonly bool IsGeographic => TryClassifyWkt(out var isGeographic)
        ? isGeographic
        : IsGeographicByWkid(Wkid);

    private readonly bool TryClassifyWkt(out bool isGeographic)
    {
        isGeographic = false;

        if (string.IsNullOrWhiteSpace(Wkt))
        {
            return false;
        }

        if (IsProjectedByWkt())
        {
            isGeographic = false;
            return true;
        }

        if (IsGeocentricByWkt())
        {
            isGeographic = false;
            return true;
        }

        if (IsGeographicByWkt())
        {
            isGeographic = true;
            return true;
        }

        return false;
    }

    private readonly bool IsProjectedByWkt() =>
        Wkt?.Contains("PROJCS", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("PROJCRS", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("PROJECTEDCRS", StringComparison.OrdinalIgnoreCase) == true ||
        (Wkt?.Contains("+proj=", StringComparison.OrdinalIgnoreCase) == true &&
         !Wkt.Contains("+proj=longlat", StringComparison.OrdinalIgnoreCase) &&
         !Wkt.Contains("+proj=latlong", StringComparison.OrdinalIgnoreCase) &&
         !Wkt.Contains("+proj=latlon", StringComparison.OrdinalIgnoreCase) &&
         !Wkt.Contains("+proj=geocent", StringComparison.OrdinalIgnoreCase));

    private readonly bool IsGeocentricByWkt() =>
        // WKT1 geocentric CRS is GEOCCS. The substring GEOCS matches neither GEOCCS nor GEOGCS.
        Wkt?.Contains("GEOCCS", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("GEOCENTRIC", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("+proj=geocent", StringComparison.OrdinalIgnoreCase) == true ||
        (Wkt?.Contains("GEODCRS", StringComparison.OrdinalIgnoreCase) == true &&
         (Wkt.Contains("CS[Cartesian", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("AXIS[\"X\"", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("AXIS[\"Y\"", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("AXIS[\"Z\"", StringComparison.OrdinalIgnoreCase)));

    private readonly bool IsGeographicByWkt() =>
        Wkt?.Contains("GEOGCS", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("GEOGCRS", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("+proj=longlat", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("+proj=latlong", StringComparison.OrdinalIgnoreCase) == true ||
        Wkt?.Contains("+proj=latlon", StringComparison.OrdinalIgnoreCase) == true ||
        (Wkt?.Contains("GEODCRS", StringComparison.OrdinalIgnoreCase) == true &&
         (Wkt.Contains("CS[ellipsoidal", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("ELLIPSOIDAL", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("AXIS[\"Longitude\"", StringComparison.OrdinalIgnoreCase) ||
          Wkt.Contains("AXIS[\"Latitude\"", StringComparison.OrdinalIgnoreCase)));

    // Keep the numeric fallback deliberately narrow. The EPSG 4xxx block contains a
    // mix of geographic, geocentric, and projected CRSes, so treating the whole range
    // as lat/lon causes wrong axis order and scale math. The canonical geographic
    // EPSG-code list lives in BoundingBox.IsGeographicSrid (single source of truth);
    // this WKID fallback delegates to it with the non-null value.
    private static bool IsGeographicByWkid(int wkid)
        => BoundingBox.IsGeographicSrid(wkid);

    public readonly bool IsProjected => !IsGeographic;

    public static SpatialReference Create(int wkid)
        => new() { Wkid = wkid };

    public static SpatialReference Create(int wkid, int? latestWkid)
        => new() { Wkid = wkid, LatestWkid = latestWkid };

    public static SpatialReference Create(int wkid, int? latestWkid, int? vcsWkid, int? latestVcsWkid, string? wkt)
        => new()
        {
            Wkid = wkid,
            LatestWkid = latestWkid,
            VcsWkid = vcsWkid,
            LatestVcsWkid = latestVcsWkid,
            Wkt = wkt
        };
}
