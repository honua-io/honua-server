// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Server.Features.Infrastructure.Services;

/// <summary>
/// Shared parsing utilities for raster-related request parameters used by ImageServer and OGC Maps.
/// </summary>
internal static class RasterParsingHelpers
{
    /// <summary>
    /// Maximum allowed length for bounding box strings to prevent DoS.
    /// </summary>
    private const int MaxBboxLength = 100;

    /// <summary>
    /// Maximum coordinate value supporting projected CRS (e.g., Web Mercator ~20,037,508).
    /// </summary>
    private const double MaxProjectedBound = 40_000_000;

    /// <summary>
    /// Safely parses a bounding box string with validation.
    /// Supports both geographic and projected coordinate systems.
    /// </summary>
    public static bool TryParseBoundingBox(string bbox, out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = maxX = maxY = 0;

        if (string.IsNullOrWhiteSpace(bbox))
        {
            return false;
        }

        if (bbox.Length > MaxBboxLength)
        {
            return false;
        }

        var parts = bbox.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }

        if (!double.TryParse(parts[0].Trim(), out minX) ||
            !double.TryParse(parts[1].Trim(), out minY) ||
            !double.TryParse(parts[2].Trim(), out maxX) ||
            !double.TryParse(parts[3].Trim(), out maxY))
        {
            return false;
        }

        if (!IsValidCoordinate(minX) || !IsValidCoordinate(minY) ||
            !IsValidCoordinate(maxX) || !IsValidCoordinate(maxY) ||
            minX >= maxX || minY >= maxY)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Validates if a coordinate is within reasonable bounds.
    /// Supports both geographic (WGS84) and projected (Web Mercator) coordinate systems.
    /// </summary>
    public static bool IsValidCoordinate(double coordinate)
    {
        return !double.IsNaN(coordinate) &&
               !double.IsInfinity(coordinate) &&
               coordinate >= -MaxProjectedBound && coordinate <= MaxProjectedBound;
    }

    /// <summary>
    /// Parses a format string to the corresponding <see cref="RasterFormat"/> enum value.
    /// </summary>
    public static RasterFormat ParseRasterFormat(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "png" => RasterFormat.PNG,
            "jpeg" or "jpg" => RasterFormat.JPEG,
            "tiff" or "tif" => RasterFormat.TIFF,
            _ => RasterFormat.PNG
        };
    }

}
