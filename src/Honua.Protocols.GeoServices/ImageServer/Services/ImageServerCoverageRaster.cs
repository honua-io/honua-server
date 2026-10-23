// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Raster.Multidimensional.Domain;

namespace Honua.Protocols.GeoServices.ImageServer.Services;

/// <summary>
/// Raster description of a scanned multidimensional coverage. Service info uses this
/// instead of a co-registered base raster so the envelope, cell size, pixel type,
/// NoData, and statistics match the grid the slices come from.
/// </summary>
internal sealed record ImageServerCoverageGrid
{
    public required RasterExtent Extent { get; init; }

    public required double PixelSizeX { get; init; }

    public required double PixelSizeY { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>PostGIS sample type (<c>32BF</c>), or null when the variable type is unrecognized.</summary>
    public string? StoredPixelType { get; init; }

    public double? NoDataValue { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? Mean { get; init; }

    public double? StandardDeviation { get; init; }

    public bool YAxisAscending { get; init; }
}

/// <summary>
/// Projects scanned coverage metadata onto the raster facts ImageServer service
/// info and key properties advertise.
/// </summary>
internal static class ImageServerCoverageRaster
{
    public static ImageServerCoverageGrid? TryDescribe(MultidimensionalCoverageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Extent is not { } extent)
        {
            return null;
        }

        if (metadata.Variables.Count == 0)
        {
            return null;
        }

        var variable = metadata.Variables[0];

        var width = AxisSize(variable, MultidimensionalAxisKind.HorizontalX);
        var height = AxisSize(variable, MultidimensionalAxisKind.HorizontalY);
        var pixelX = metadata.Resolution.X;
        var pixelY = metadata.Resolution.Y;
        var spanX = extent.XMax - extent.XMin;
        var spanY = extent.YMax - extent.YMin;
        if (pixelX <= 0 && width > 0 && spanX > 0)
        {
            pixelX = spanX / width;
        }

        if (pixelY <= 0 && height > 0 && spanY > 0)
        {
            pixelY = spanY / height;
        }

        if (width <= 0 && pixelX > 0 && spanX > 0)
        {
            width = (int)Math.Round(spanX / pixelX, MidpointRounding.AwayFromZero);
        }

        if (height <= 0 && pixelY > 0 && spanY > 0)
        {
            height = (int)Math.Round(spanY / pixelY, MidpointRounding.AwayFromZero);
        }

        if (pixelX <= 0 || pixelY <= 0 || width <= 0 || height <= 0 ||
            !double.IsFinite(pixelX) || !double.IsFinite(pixelY))
        {
            return null;
        }

        return new ImageServerCoverageGrid
        {
            Extent = extent,
            PixelSizeX = pixelX,
            PixelSizeY = pixelY,
            Width = width,
            Height = height,
            StoredPixelType = ToStoredPixelType(variable.DataType),
            NoDataValue = variable.NoData,
            Minimum = metadata.Statistics?.Minimum,
            Maximum = metadata.Statistics?.Maximum,
            Mean = metadata.Statistics?.Mean,
            StandardDeviation = metadata.Statistics?.StandardDeviation,
            YAxisAscending = metadata.YAxisAscending,
        };
    }

    public static RasterInfo Apply(RasterInfo raster, ImageServerCoverageGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        var originY = grid.YAxisAscending ? grid.Extent.YMin : grid.Extent.YMax;
        var pixelHeight = grid.YAxisAscending ? grid.PixelSizeY : -grid.PixelSizeY;
        return raster with
        {
            Width = grid.Width,
            Height = grid.Height,
            BandCount = 1,
            PixelType = grid.StoredPixelType ?? raster.PixelType,
            NoDataValue = grid.NoDataValue,
            HasUniformNoDataValue = grid.NoDataValue is not null,
            Srid = grid.Extent.Srid ?? raster.Srid,
            Extent = grid.Extent,
            GeoTransform = [grid.Extent.XMin, grid.PixelSizeX, 0, originY, 0, pixelHeight],
        };
    }

    public static RasterStatistics[] Statistics(ImageServerCoverageGrid grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (grid.Minimum is not double minimum || grid.Maximum is not double maximum)
        {
            return [];
        }

        return
        [
            new RasterStatistics
            {
                Band = 1,
                MinValue = minimum,
                MaxValue = maximum,
                MeanValue = grid.Mean,
                StandardDeviation = grid.StandardDeviation,
            }
        ];
    }

    private static int AxisSize(MultidimensionalCoverageVariable variable, MultidimensionalAxisKind axis)
    {
        foreach (var dimension in variable.Dimensions)
        {
            if (dimension.Axis == axis && dimension.Size is > 0 and <= int.MaxValue)
            {
                return (int)dimension.Size;
            }
        }

        return 0;
    }

    private static string? ToStoredPixelType(string dataType)
        => dataType.Trim().ToUpperInvariant() switch
        {
            "FLOAT32" or "FLOAT" or "32BF" or "F32" => "32BF",
            "FLOAT64" or "DOUBLE" or "64BF" or "F64" => "64BF",
            "BYTE" or "UINT8" or "8BUI" or "U8" => "8BUI",
            "INT8" or "SBYTE" or "8BSI" or "S8" => "8BSI",
            "UINT16" or "16BUI" or "U16" => "16BUI",
            "INT16" or "16BSI" or "S16" => "16BSI",
            "UINT32" or "32BUI" or "U32" => "32BUI",
            "INT32" or "32BSI" or "S32" => "32BSI",
            _ => null,
        };
}
