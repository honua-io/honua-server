// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Shared.Models;

public static class SpatialConstants
{
    public const double WebMercatorExtent = 20037508.342789244;

    public const double WebMercatorMaxLatitude = 85.0511287798066;

    public const double EarthRadius = 6378137.0;

    // The OGC pixel size is 0.28 mm and is the input to scale-denominator calculations.
    public const double PixelSizeMeters = 0.00028;

    public const double DegreesPerPixel = PixelSizeMeters * 180.0 / WebMercatorExtent;

    // Distance conversion factors to meters
    public const double FeetToMeters = 0.3048;

    public const double MilesToMeters = 1609.344;

    public const double KilometersToMeters = 1000.0;

    public const double YardsToMeters = 0.9144;

    public const int DefaultSrid = 4326;

    // The former SpatialConstants.GeographicSrids shim was removed in #2732 follow-ups: it held the
    // narrow geodesic-safe subset under a misleadingly broad name and had no production consumers.
    // Call GeographicSridClassifier.GeodesicDistanceSafeSrids / IsGeodesicDistanceSafeSrid (geodesic
    // routing) or GeographicSridClassifier.IsGeographicSrid (axis order / degree routing) directly.
}
