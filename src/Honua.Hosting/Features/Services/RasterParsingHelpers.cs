// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Validation;

namespace Honua.Infrastructure.Services;

internal static class RasterParsingHelpers
{
    private const int MaxBboxLength = 1024;

    private const double MaxProjectedBound = 40_000_000;

    private const double DegenerateWidthEpsilon = 1e-9;

    public static bool TryParseBoundingBox(string bbox, out double minX, out double minY, out double maxX, out double maxY)
        => TryParseBoundingBox(bbox, AxisOrder.EastNorth, isGeographic: false, out minX, out minY, out maxX, out maxY);

    public static bool TryParseBoundingBox(
        string bbox,
        AxisOrder axisOrder,
        bool isGeographic,
        out double minX,
        out double minY,
        out double maxX,
        out double maxY)
    {
        minX = minY = maxX = maxY = 0;

        if (bbox is null || bbox.Length > MaxBboxLength)
        {
            return false;
        }

        var parsed = BoundingBoxOrdinates.Parse(bbox);
        if (!parsed.Succeeded || parsed.Ordinates.Length != 4)
        {
            return false;
        }

        var (first, second, third, fourth) = BoundingBoxOrdinates.Horizontal(parsed.Ordinates);
        if (axisOrder == AxisOrder.NorthEast)
        {
            minX = second;
            minY = first;
            maxX = fourth;
            maxY = third;
        }
        else
        {
            minX = first;
            minY = second;
            maxX = third;
            maxY = fourth;
        }

        if (!IsValidCoordinate(minX) || !IsValidCoordinate(minY) ||
            !IsValidCoordinate(maxX) || !IsValidCoordinate(maxY) ||
            minY >= maxY ||
            (!isGeographic && minX >= maxX) ||
            (isGeographic && Math.Abs(minX - maxX) < DegenerateWidthEpsilon))
        {
            return false;
        }

        if (isGeographic &&
            (!IsValidLongitude(minX) || !IsValidLongitude(maxX) ||
             !IsValidLatitude(minY) || !IsValidLatitude(maxY)))
        {
            return false;
        }

        return true;
    }

    public static bool IsValidCoordinate(double coordinate)
    {
        return !double.IsNaN(coordinate) &&
               !double.IsInfinity(coordinate) &&
               coordinate >= -MaxProjectedBound && coordinate <= MaxProjectedBound;
    }

    private static bool IsValidLongitude(double coordinate) => coordinate >= -180.0 && coordinate <= 180.0;

    private static bool IsValidLatitude(double coordinate) => coordinate >= -90.0 && coordinate <= 90.0;

    public static RasterFormat ParseRasterFormat(string? format)
    {
        return TryParseRasterFormat(format, out var rasterFormat)
            ? rasterFormat
            : RasterFormat.PNG;
    }

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
            case "cog":
                rasterFormat = RasterFormat.COG;
                return true;
            default:
                rasterFormat = RasterFormat.PNG;
                return false;
        }
    }

}
