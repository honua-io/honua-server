// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Represents a spatial reference system with various identifier formats
/// </summary>
public readonly record struct SpatialReference
{
    /// <summary>
    /// Well-Known ID (EPSG code)
    /// </summary>
    public required int Wkid { get; init; }

    /// <summary>
    /// Latest Well-Known ID (for newer EPSG codes)
    /// </summary>
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Vertical coordinate system WKID
    /// </summary>
    public int? VcsWkid { get; init; }

    /// <summary>
    /// Latest vertical coordinate system WKID
    /// </summary>
    public int? LatestVcsWkid { get; init; }

    /// <summary>
    /// Well-Known Text representation
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// Alias for Wkid to maintain backward compatibility
    /// </summary>
    public int Srid => Wkid;

    /// <summary>
    /// Alias for Wkt to maintain backward compatibility
    /// </summary>
    public string? WellKnownText => Wkt;

    /// <summary>
    /// WGS84 Geographic coordinate system (EPSG:4326)
    /// Most commonly used for web mapping and GPS coordinates
    /// </summary>
    public static readonly SpatialReference WGS84 = new()
    {
        Wkid = 4326,
        Wkt = "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]"
    };

    /// <summary>
    /// Web Mercator projection (EPSG:3857)
    /// Used by most web mapping services (Google Maps, OpenStreetMap)
    /// </summary>
    public static readonly SpatialReference WebMercator = new()
    {
        Wkid = 3857,
        Wkt = "PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Mercator_1SP\"],PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1],PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1]]"
    };

    /// <summary>
    /// Display name for the spatial reference system
    /// </summary>
    public readonly string DisplayName => Wkid switch
    {
        4326 => "WGS 84 (Geographic)",
        3857 => "WGS 84 / Web Mercator",
        _ => $"EPSG:{Wkid}"
    };

    /// <summary>
    /// Whether this is a geographic (lat/lon) coordinate system.
    /// Checks WKT first (most reliable), then falls back to EPSG code heuristics.
    /// </summary>
    public readonly bool IsGeographic => IsProjectedByWkt()
        ? false
        : IsGeographicByWkt() || IsGeographicByWkid(Wkid);

    private readonly bool IsProjectedByWkt() =>
        Wkt?.Contains("PROJCS", StringComparison.Ordinal) == true ||
        Wkt?.Contains("PROJCRS", StringComparison.Ordinal) == true;

    private readonly bool IsGeographicByWkt() =>
        Wkt?.Contains("GEOGCS", StringComparison.Ordinal) == true ||
        Wkt?.Contains("GEOGCRS", StringComparison.Ordinal) == true ||
        Wkt?.Contains("GEODCRS", StringComparison.Ordinal) == true;

    private static bool IsGeographicByWkid(int wkid)
        => wkid is 4326 or 4269 or 4267 or 4258 or 4979;

    /// <summary>
    /// Whether this is a projected coordinate system
    /// </summary>
    public readonly bool IsProjected => !IsGeographic;

    /// <summary>
    /// Creates a spatial reference with only WKID
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <returns>Spatial reference instance</returns>
    public static SpatialReference Create(int wkid)
        => new() { Wkid = wkid };

    /// <summary>
    /// Creates a spatial reference with WKID and latest WKID
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <param name="latestWkid">Latest Well-Known ID</param>
    /// <returns>Spatial reference instance</returns>
    public static SpatialReference Create(int wkid, int? latestWkid)
        => new() { Wkid = wkid, LatestWkid = latestWkid };

    /// <summary>
    /// Creates a spatial reference with all parameters
    /// </summary>
    /// <param name="wkid">Well-Known ID (EPSG code)</param>
    /// <param name="latestWkid">Latest Well-Known ID</param>
    /// <param name="vcsWkid">Vertical coordinate system WKID</param>
    /// <param name="latestVcsWkid">Latest vertical coordinate system WKID</param>
    /// <param name="wkt">Well-Known Text representation</param>
    /// <returns>Spatial reference instance</returns>
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
