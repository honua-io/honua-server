// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.Shared.Models;

/// <summary>
/// Provides standardized distance unit conversions to eliminate magic numbers
/// and ensure consistent distance calculations across the codebase.
/// </summary>
public static class DistanceConversions
{
    /// <summary>
    /// Converts a distance from the specified unit to meters.
    /// </summary>
    /// <param name="distance">The distance value to convert.</param>
    /// <param name="unit">The unit of the input distance.</param>
    /// <returns>The distance converted to meters.</returns>
    public static double ToMeters(double distance, DistanceUnit unit) =>
        unit switch
        {
            DistanceUnit.Meters => distance,
            DistanceUnit.Feet => distance * SpatialConstants.FeetToMeters,
            DistanceUnit.Kilometers => distance * SpatialConstants.KilometersToMeters,
            DistanceUnit.Miles => distance * SpatialConstants.MilesToMeters,
            _ => distance
        };

    /// <summary>
    /// Converts a distance from the specified unit string to meters.
    /// Used for text-based APIs and natural language processing.
    /// </summary>
    /// <param name="distance">The distance value to convert.</param>
    /// <param name="unitString">The unit name (case-insensitive).</param>
    /// <returns>The distance converted to meters.</returns>
    public static double ToMeters(double distance, string unitString) =>
        unitString.ToLowerInvariant() switch
        {
            "meters" or "m" or "meter" => distance,
            "feet" or "ft" or "foot" => distance * SpatialConstants.FeetToMeters,
            "kilometers" or "km" or "kilometer" => distance * SpatialConstants.KilometersToMeters,
            "miles" or "mi" or "mile" => distance * SpatialConstants.MilesToMeters,
            _ => distance
        };

    /// <summary>
    /// Determines if the specified SRID represents a geographic (latitude/longitude) coordinate system.
    /// </summary>
    /// <param name="srid">The spatial reference identifier to check.</param>
    /// <returns>True if the SRID is geographic, false if it's projected or unknown.</returns>
    public static bool IsGeographicSrid(int srid) =>
        Array.IndexOf(SpatialConstants.GeographicSrids, srid) >= 0;
}