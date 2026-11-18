// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.ZarrParser;

public sealed record ZarrGeoTiffOutput
{
    public required RasterExtent Extent { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }
}

// Encodes one planned 2D Zarr slice as a single-band GeoTIFF on a caller-chosen north-up grid.
// Samples keep the source dtype (nearest neighbour, no value conversion); output pixels whose
// centre falls outside the read window carry the array fill value, advertised as GDAL nodata.
public static class ZarrGeoTiffRenderer
{
    public static byte[] Render(ZarrSubsetResult result, ZarrTileSlicePlan slice, ZarrGeoTiffOutput output)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(slice);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(output.Height);

        var dtype = ZarrTileRenderer.Normalize(result.DataType);
        var (bits, sampleFormat) = ResolveSampleLayout(dtype, result.DataType);
        var elementSize = bits / 8;
        var sourceWidth = result.Shape[slice.XDimensionIndex];
        var sourceHeight = result.Shape[slice.YDimensionIndex];
        if (sourceWidth <= 0 || sourceHeight <= 0 || result.Data.LongLength < (long)sourceWidth * sourceHeight * elementSize)
        {
            throw new InvalidDataException("The Zarr slice buffer does not match its planned spatial window.");
        }

        var fillValue = ResolveFillValue(dtype, slice.Plan.Array.FillValue);
        var samples = new byte[checked(output.Width * output.Height * elementSize)];
        ZarrFillValueCodec.Fill(samples, dtype, elementSize, fillValue, CancellationToken.None);
        CopyNearestSamples(result, slice, (output, samples));

        return GeoTiffStripEncoder.Encode(new GeoTiffStripImage
        {
            LittleEndianSamples = samples,
            Width = output.Width,
            Height = output.Height,
            BitsPerSample = bits,
            SampleFormat = sampleFormat,
            Extent = output.Extent,
            NoData = FormatNoData(dtype, fillValue)
        });
    }

    public static bool SupportsDataType(string dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        return ZarrTileRenderer.Normalize(dataType) is "u1" or "b1" or "i1" or "u2" or "i2" or "u4" or "i4" or "f4" or "u8" or "i8" or "f8";
    }

    public static long MeasureOutputBytes(ZarrTileSlicePlan slice, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(slice);
        var dataType = slice.Plan.Array.DataType;
        var (bits, _) = ResolveSampleLayout(ZarrTileRenderer.Normalize(dataType), dataType);
        return (long)width * height * (bits / 8);
    }

    private static void CopyNearestSamples(
        ZarrSubsetResult result,
        ZarrTileSlicePlan slice,
        (ZarrGeoTiffOutput Grid, byte[] Samples) output)
    {
        var grid = output.Grid;
        var elementSize = output.Samples.Length / (grid.Width * grid.Height);
        var (strideY, strideX) = ZarrTileRenderer.ComputeSpatialStrides(
            result.Shape, slice.YDimensionIndex, slice.XDimensionIndex);
        var columns = MapColumns(slice, grid, result.Shape[slice.XDimensionIndex]);
        var rows = MapRows(slice, grid, result.Shape[slice.YDimensionIndex]);
        var rowLength = grid.Width * elementSize;
        for (var row = 0; row < grid.Height; row++)
        {
            var sourceRow = rows[row];
            if (sourceRow >= 0)
            {
                CopyRow(
                    result.Data.AsSpan(sourceRow * strideY * elementSize),
                    output.Samples.AsSpan(row * rowLength, rowLength),
                    (columns, strideX * elementSize, elementSize));
            }
        }
    }

    // Source column for each output pixel centre, or -1 when the centre is outside the window.
    private static int[] MapColumns(ZarrTileSlicePlan slice, ZarrGeoTiffOutput grid, int sourceWidth)
    {
        var pixelWidth = (grid.Extent.XMax - grid.Extent.XMin) / grid.Width;
        var columns = new int[grid.Width];
        for (var column = 0; column < grid.Width; column++)
        {
            var x = grid.Extent.XMin + ((column + 0.5) * pixelWidth);
            var sourceColumn = (int)Math.Floor((x - slice.GridXMin) / slice.CellWidth);
            columns[column] = sourceColumn >= 0 && sourceColumn < sourceWidth ? sourceColumn : -1;
        }

        return columns;
    }

    // Source row for each output row centre (output rows run north to south), or -1 when outside.
    private static int[] MapRows(ZarrTileSlicePlan slice, ZarrGeoTiffOutput grid, int sourceHeight)
    {
        var pixelHeight = (grid.Extent.YMax - grid.Extent.YMin) / grid.Height;
        var gridYMin = slice.GridYMax - (sourceHeight * slice.CellHeight);
        var rows = new int[grid.Height];
        for (var row = 0; row < grid.Height; row++)
        {
            var y = grid.Extent.YMax - ((row + 0.5) * pixelHeight);
            var sourceRow = (int)Math.Floor(slice.YAxisAscending
                ? (y - gridYMin) / slice.CellHeight
                : (slice.GridYMax - y) / slice.CellHeight);
            rows[row] = sourceRow >= 0 && sourceRow < sourceHeight ? sourceRow : -1;
        }

        return rows;
    }

    private static void CopyRow(
        ReadOnlySpan<byte> sourceRow,
        Span<byte> targetRow,
        (int[] Columns, int ColumnStride, int ElementSize) layout)
    {
        for (var column = 0; column < layout.Columns.Length; column++)
        {
            var sourceColumn = layout.Columns[column];
            if (sourceColumn >= 0)
            {
                sourceRow.Slice(sourceColumn * layout.ColumnStride, layout.ElementSize)
                    .CopyTo(targetRow.Slice(column * layout.ElementSize, layout.ElementSize));
            }
        }
    }

    private static (int Bits, ushort SampleFormat) ResolveSampleLayout(string dtype, string original) => dtype switch
    {
        "u1" or "b1" => (8, TiffConstants.SampleFormatUnsigned),
        "i1" => (8, TiffConstants.SampleFormatSigned),
        "u2" => (16, TiffConstants.SampleFormatUnsigned),
        "i2" => (16, TiffConstants.SampleFormatSigned),
        "u4" => (32, TiffConstants.SampleFormatUnsigned),
        "i4" => (32, TiffConstants.SampleFormatSigned),
        "f4" => (32, TiffConstants.SampleFormatFloat),
        "u8" => (64, TiffConstants.SampleFormatUnsigned),
        "i8" => (64, TiffConstants.SampleFormatSigned),
        "f8" => (64, TiffConstants.SampleFormatFloat),
        _ => throw new InvalidOperationException($"Zarr dtype '{original}' cannot be encoded as GeoTIFF.")
    };

    // Floating-point grids without a declared fill use NaN for uncovered pixels; integer grids
    // without one use zero and advertise no nodata value.
    private static object? ResolveFillValue(string dtype, object? declared)
        => declared ?? (dtype is "f4" or "f8" ? double.NaN : null);

    private static string? FormatNoData(string dtype, object? fillValue)
    {
        if (ZarrValueDecoder.TryGetFiniteFillValue(fillValue, out var finite))
        {
            return finite.ToString("G17", CultureInfo.InvariantCulture);
        }

        return fillValue switch
        {
            long signed => signed.ToString(CultureInfo.InvariantCulture),
            ulong unsigned => unsigned.ToString(CultureInfo.InvariantCulture),
            double number when double.IsPositiveInfinity(number) => "inf",
            float single when float.IsPositiveInfinity(single) => "inf",
            double number when double.IsNegativeInfinity(number) => "-inf",
            float single when float.IsNegativeInfinity(single) => "-inf",
            _ when dtype is "f4" or "f8" => "nan",
            _ => null
        };
    }
}
