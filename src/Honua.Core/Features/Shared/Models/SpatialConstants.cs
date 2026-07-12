// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Canonical spatial constants used across tile, map, and coordinate operations.
/// All Web Mercator and geographic projection values should reference this class
/// to avoid subtle discrepancies from duplicate definitions.
/// </summary>
public static class SpatialConstants
{
    /// <summary>
    /// Web Mercator (EPSG:3857) half-circumference in meters.
    /// Equal to the semi-major axis (6378137 m) multiplied by pi.
    /// </summary>
    public const double WebMercatorExtent = 20037508.342789244;

    /// <summary>
    /// Maximum latitude representable in Web Mercator projection (degrees).
    /// Beyond this latitude the Mercator projection diverges to infinity.
    /// </summary>
    public const double WebMercatorMaxLatitude = 85.0511287798066;

    /// <summary>
    /// WGS 84 semi-major axis (equatorial radius) in meters.
    /// </summary>
    public const double EarthRadius = 6378137.0;

    /// <summary>
    /// OGC standard pixel size in meters (0.28 mm per pixel).
    /// Used for scale denominator calculations.
    /// </summary>
    public const double PixelSizeMeters = 0.00028;

    /// <summary>
    /// Cell size in degrees per pixel at the equator (derived OGC value).
    /// Computed as <c>PixelSizeMeters * 180 / WebMercatorExtent</c>.
    /// </summary>
    public const double DegreesPerPixel = PixelSizeMeters * 180.0 / WebMercatorExtent;

    // Distance conversion factors to meters
    /// <summary>
    /// Conversion factor from feet to meters (1 foot = 0.3048 meters).
    /// </summary>
    public const double FeetToMeters = 0.3048;

    /// <summary>
    /// Conversion factor from miles to meters (1 mile = 1609.344 meters).
    /// </summary>
    public const double MilesToMeters = 1609.344;

    /// <summary>
    /// Conversion factor from kilometers to meters (1 kilometer = 1000 meters).
    /// </summary>
    public const double KilometersToMeters = 1000.0;

    /// <summary>
    /// Conversion factor from yards to meters (1 yard = 0.9144 meters).
    /// </summary>
    public const double YardsToMeters = 0.9144;

    /// <summary>
    /// Default spatial reference identifier (WGS 84).
    /// </summary>
    public const int DefaultSrid = 4326;

    // Well-known geographic SRIDs for distance function selection
    /// <summary>
    /// Narrow allowlist of geographic (latitude/longitude degree) SRIDs used to select
    /// WGS 84 spheroid geodesic distance functions. This is the geodesic-safe subset and is
    /// intentionally narrower than the full geographic list; it is now sourced from the
    /// canonical <see cref="GeographicSridClassifier.GeodesicDistanceSafeSrids"/> (#2732).
    /// Prefer <see cref="GeographicSridClassifier"/> directly for new call sites.
    /// </summary>
    public static readonly int[] GeographicSrids = [.. GeographicSridClassifier.GeodesicDistanceSafeSrids];
}
