// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

public static class DistanceConversions
{
    public static double ToMeters(double distance, DistanceUnit unit) =>
        unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Feet => distance * SpatialConstants.FeetToMeters,
            DistanceUnit.Kilometers => distance * SpatialConstants.KilometersToMeters,
            DistanceUnit.Miles => distance * SpatialConstants.MilesToMeters,
            _ => distance
        };

    public static double ToMeters(double distance, string unitString) =>
        unitString.ToLowerInvariant() switch
        {
            "meters" or "m" or "meter" => distance,
            "feet" or "ft" or "foot" => distance * SpatialConstants.FeetToMeters,
            "kilometers" or "km" or "kilometer" => distance * SpatialConstants.KilometersToMeters,
            "miles" or "mi" or "mile" => distance * SpatialConstants.MilesToMeters,
            _ => distance
        };

    public static bool IsGeographicSrid(int srid) =>
        GeographicSridClassifier.IsGeodesicDistanceSafeSrid(srid);
}
