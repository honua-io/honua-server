// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Configuration;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Infrastructure.Security;

/// <summary>
/// Provides WKB (Well-Known Binary) geometry validation for input security.
/// Used to validate geometry data from OGC API Features and OData endpoints.
/// Validation limits are configured via <see cref="GeometryValidationOptions"/>.
/// </summary>
public static class WkbValidation
{
    private static readonly WKBReader _wkbReader = new();

    /// <summary>
    /// Validates WKB geometry data using limits from configuration.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <param name="options">Validation limits configuration. Uses defaults if null.</param>
    /// <returns>Validation result with error message if invalid.</returns>
    public static WkbValidationResult Validate(byte[]? wkb, GeometryValidationOptions? options = null)
    {
        var validationOptions = options ?? new GeometryValidationOptions();
        return Validate(wkb, validationOptions.MaxWkbSize, validationOptions.MaxVertices, validationOptions.MaxRings);
    }

    /// <summary>
    /// Validates WKB geometry data for security and size limits.
    /// </summary>
    /// <param name="wkb">The WKB bytes to validate.</param>
    /// <param name="maxWkbSize">Maximum allowed WKB size in bytes.</param>
    /// <param name="maxVertices">Maximum allowed vertices.</param>
    /// <param name="maxRings">Maximum allowed rings for polygon geometries.</param>
    /// <returns>Validation result with error message if invalid.</returns>
    public static WkbValidationResult Validate(
        byte[]? wkb,
        long maxWkbSize,
        int maxVertices,
        int maxRings)
    {
        // Null geometry is valid (feature without geometry)
        if (wkb == null || wkb.Length == 0)
        {
            return WkbValidationResult.Valid();
        }

        // Check WKB size
        if (wkb.Length > maxWkbSize)
        {
            return WkbValidationResult.Invalid(
                $"Geometry size ({wkb.Length:N0} bytes) exceeds maximum allowed ({maxWkbSize:N0} bytes)");
        }

        // Parse and validate geometry structure
        Geometry geometry;
        try
        {
            geometry = _wkbReader.Read(wkb);
        }
        catch (Exception)
        {
            return WkbValidationResult.Invalid("Invalid WKB format.");
        }

        if (geometry == null)
        {
            return WkbValidationResult.Invalid("Failed to parse WKB geometry");
        }

        // Validate vertex count
        var vertexCount = geometry.NumPoints;
        if (vertexCount > maxVertices)
        {
            return WkbValidationResult.Invalid(
                $"Geometry vertex count ({vertexCount:N0}) exceeds maximum allowed ({maxVertices:N0})");
        }

        // Validate ring count for polygon geometries
        var ringCount = CountRings(geometry);
        if (ringCount > maxRings)
        {
            return WkbValidationResult.Invalid(
                $"Geometry ring count ({ringCount:N0}) exceeds maximum allowed ({maxRings:N0})");
        }

        // Validate coordinate values
        if (!ValidateCoordinates(geometry))
        {
            return WkbValidationResult.Invalid("Geometry contains invalid coordinates (NaN or Infinity)");
        }

        return WkbValidationResult.Valid();
    }

    /// <summary>
    /// Counts the total number of rings in polygon geometries.
    /// </summary>
    private static int CountRings(Geometry geometry)
    {
        return geometry switch
        {
            Polygon polygon => 1 + polygon.NumInteriorRings,
            MultiPolygon multiPolygon => multiPolygon.Geometries
                .OfType<Polygon>()
                .Sum(p => 1 + p.NumInteriorRings),
            GeometryCollection collection => collection.Geometries
                .Sum(CountRings),
            _ => 0
        };
    }

    /// <summary>
    /// Validates that all coordinates in the geometry are finite numbers.
    /// </summary>
    private static bool ValidateCoordinates(Geometry geometry)
    {
        foreach (var coord in geometry.Coordinates)
        {
            if (double.IsNaN(coord.X) || double.IsInfinity(coord.X) ||
                double.IsNaN(coord.Y) || double.IsInfinity(coord.Y))
            {
                return false;
            }

            // Check Z if present
            if (!double.IsNaN(coord.Z) && double.IsInfinity(coord.Z))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Result of WKB validation.
/// </summary>
public readonly struct WkbValidationResult
{
    /// <summary>
    /// Whether the WKB is valid.
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Error message if validation failed.
    /// </summary>
    public string? ErrorMessage { get; }

    private WkbValidationResult(bool isValid, string? errorMessage)
    {
        IsValid = isValid;
        ErrorMessage = errorMessage;
    }

    /// <summary>
    /// Creates a valid result.
    /// </summary>
    public static WkbValidationResult Valid() => new(true, null);

    /// <summary>
    /// Creates an invalid result with the specified error message.
    /// </summary>
    public static WkbValidationResult Invalid(string errorMessage) => new(false, errorMessage);
}
