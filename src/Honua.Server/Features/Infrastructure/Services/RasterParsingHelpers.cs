// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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

        if (!double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minX) ||
            !double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out minY) ||
            !double.TryParse(parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maxX) ||
            !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out maxY))
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
    public static RasterFormat ParseRasterFormat(string? format)
    {
        return TryParseRasterFormat(format, out var rasterFormat)
            ? rasterFormat
            : RasterFormat.PNG;
    }

    /// <summary>
    /// Tries to parse a raster format string.
    /// </summary>
    /// <param name="format">Requested format value.</param>
    /// <param name="rasterFormat">Parsed output format when successful.</param>
    /// <returns>True when the format is explicitly supported, otherwise false.</returns>
    public static bool TryParseRasterFormat(string? format, out RasterFormat rasterFormat)
    {
        if (string.IsNullOrEmpty(format))
        {
            rasterFormat = RasterFormat.PNG;
            return true;
        }

        switch (format.ToLowerInvariant())
        {
            case "png":
                rasterFormat = RasterFormat.PNG;
                return true;
            case "jpeg":
            case "jpg":
                rasterFormat = RasterFormat.JPEG;
                return true;
            case "tiff":
            case "tif":
                rasterFormat = RasterFormat.TIFF;
                return true;
            default:
                rasterFormat = RasterFormat.PNG;
                return false;
        }
    }

}
