// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

namespace Honua.Mobile.Core.Models;

/// <summary>
/// Represents a spatial reference system (coordinate system).
/// </summary>
public sealed record SpatialReference
{
    /// <summary>
    /// Well-Known ID (WKID) of the coordinate system.
    /// </summary>
    public int? Wkid { get; init; }

    /// <summary>
    /// Latest WKID for coordinate systems that have been updated.
    /// </summary>
    public int? LatestWkid { get; init; }

    /// <summary>
    /// Well-Known Text representation of the coordinate system.
    /// </summary>
    public string? Wkt { get; init; }

    /// <summary>
    /// Creates a spatial reference from a WKID.
    /// </summary>
    public static SpatialReference FromWkid(int wkid, int? latestWkid = null)
    {
        return new SpatialReference
        {
            Wkid = wkid,
            LatestWkid = latestWkid
        };
    }

    /// <summary>
    /// Creates a spatial reference from Well-Known Text.
    /// </summary>
    public static SpatialReference FromWkt(string wkt)
    {
        return new SpatialReference { Wkt = wkt };
    }

    /// <summary>
    /// Common spatial reference systems.
    /// </summary>
    public static class Common
    {
        /// <summary>
        /// WGS 84 Geographic (EPSG:4326) - commonly used for GPS coordinates.
        /// </summary>
        public static SpatialReference Wgs84 => FromWkid(4326);

        /// <summary>
        /// Web Mercator (EPSG:3857) - used by most web mapping services.
        /// </summary>
        public static SpatialReference WebMercator => FromWkid(3857, 102100);

        /// <summary>
        /// WGS 84 / UTM Zone 10N (EPSG:32610) - common for western US.
        /// </summary>
        public static SpatialReference UtmZone10N => FromWkid(32610);
    }
}