// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Domain.Catalog;

/// <summary>
/// Spatial reference system information for coordinate systems
/// </summary>
/// <param name="Srid">Spatial Reference System Identifier (EPSG code)</param>
/// <param name="WellKnownText">Well-Known Text representation (optional)</param>
public record SpatialReference(
    int Srid,
    string? WellKnownText = null)
{
    /// <summary>
    /// WGS84 Geographic coordinate system (EPSG:4326)
    /// Most commonly used for web mapping and GPS coordinates
    /// </summary>
    public static readonly SpatialReference WGS84 = new(4326, "GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]]");

    /// <summary>
    /// Web Mercator projection (EPSG:3857)
    /// Used by most web mapping services (Google Maps, OpenStreetMap)
    /// </summary>
    public static readonly SpatialReference WebMercator = new(3857, "PROJCS[\"WGS 84 / Pseudo-Mercator\",GEOGCS[\"WGS 84\",DATUM[\"WGS_1984\",SPHEROID[\"WGS 84\",6378137,298.257223563]],PRIMEM[\"Greenwich\",0],UNIT[\"degree\",0.0174532925199433]],PROJECTION[\"Mercator_1SP\"],PARAMETER[\"central_meridian\",0],PARAMETER[\"scale_factor\",1],PARAMETER[\"false_easting\",0],PARAMETER[\"false_northing\",0],UNIT[\"metre\",1]]");

    /// <summary>
    /// Display name for the spatial reference system
    /// </summary>
    public string DisplayName => Srid switch
    {
        4326 => "WGS 84 (Geographic)",
        3857 => "WGS 84 / Web Mercator",
        _ => $"EPSG:{Srid}"
    };

    /// <summary>
    /// Whether this is a geographic (lat/lon) coordinate system
    /// </summary>
    public bool IsGeographic => Srid == 4326 || (WellKnownText?.Contains("GEOGCS") == true);

    /// <summary>
    /// Whether this is a projected coordinate system
    /// </summary>
    public bool IsProjected => !IsGeographic;
}
