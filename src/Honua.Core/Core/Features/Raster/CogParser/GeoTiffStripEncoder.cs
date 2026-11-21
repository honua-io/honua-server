// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Raster.CogParser;

public sealed record GeoTiffStripImage
{
    public required byte[] LittleEndianSamples { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required int BitsPerSample { get; init; }

    public required ushort SampleFormat { get; init; }

    public required RasterExtent Extent { get; init; }

    public string? NoData { get; init; }
}

public static class GeoTiffStripEncoder
{
    private const ushort TagStripOffsets = 273;
    private const ushort TagRowsPerStrip = 278;
    private const ushort TagStripByteCounts = 279;
    private const ushort TagPlanarConfiguration = 284;
    private const ushort TagGdalNoData = 42113;

    private const ushort GeoKeyModelType = 1024;
    private const ushort ModelTypeProjected = 1;
    private const ushort ModelTypeGeographic = 2;
    private const ushort RasterPixelIsArea = 1;
    private const ushort PhotometricBlackIsZero = 1;
    private const ushort PlanarContiguous = 1;

    public static byte[] Encode(GeoTiffStripImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Validate(image);

        var fields = CreateFields(image);
        var externalOffset = TiffConstants.ClassicHeaderSize + 2 + (fields.Count * TiffConstants.ClassicIfdEntrySize) + 4;
        var stripOffset = externalOffset + fields.Where(static field => field.Value.Length > 4)
            .Sum(static field => (field.Value.Length + 1) & ~1);

        using var stream = new MemoryStream(checked(stripOffset + image.LittleEndianSamples.Length));
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(TiffConstants.LittleEndianMarker);
        writer.Write(TiffConstants.ClassicTiffMagic);
        writer.Write((uint)TiffConstants.ClassicHeaderSize);
        WriteDirectory(writer, fields, (externalOffset, stripOffset));
        writer.Write(image.LittleEndianSamples);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteDirectory(
        BinaryWriter writer,
        List<(ushort Tag, ushort Type, uint Count, byte[] Value)> fields,
        (int ExternalOffset, int StripOffset) layout)
    {
        var externalOffset = layout.ExternalOffset;
        writer.Write((ushort)fields.Count);
        foreach (var field in fields)
        {
            writer.Write(field.Tag);
            writer.Write(field.Type);
            writer.Write(field.Count);
            if (field.Tag == TagStripOffsets)
            {
                writer.Write((uint)layout.StripOffset);
            }
            else if (field.Value.Length <= 4)
            {
                writer.Write(field.Value);
                writer.Write(new byte[4 - field.Value.Length]);
            }
            else
            {
                writer.Write((uint)externalOffset);
                externalOffset += (field.Value.Length + 1) & ~1;
            }
        }

        writer.Write(0u);
        foreach (var field in fields.Where(static field => field.Value.Length > 4))
        {
            writer.Write(field.Value);
            if (field.Value.Length % 2 != 0) writer.Write((byte)0);
        }
    }

    private static void Validate(GeoTiffStripImage image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            throw new ArgumentException("GeoTIFF dimensions must be positive.", nameof(image));
        }

        if (image.BitsPerSample is not (8 or 16 or 32 or 64) ||
            image.SampleFormat is not (TiffConstants.SampleFormatUnsigned or TiffConstants.SampleFormatSigned or TiffConstants.SampleFormatFloat) ||
            (image.SampleFormat == TiffConstants.SampleFormatFloat && image.BitsPerSample is not (32 or 64)))
        {
            throw new ArgumentException("GeoTIFF sample layout is not supported.", nameof(image));
        }

        var expected = checked((long)image.Width * image.Height * (image.BitsPerSample / 8));
        if (image.LittleEndianSamples.LongLength != expected)
        {
            throw new ArgumentException("GeoTIFF sample buffer does not match its pixel layout.", nameof(image));
        }
    }

    private static List<(ushort Tag, ushort Type, uint Count, byte[] Value)> CreateFields(GeoTiffStripImage image)
    {
        var extent = image.Extent;
        var fields = new List<(ushort Tag, ushort Type, uint Count, byte[] Value)>
        {
            (TiffConstants.TagImageWidth, TiffConstants.TypeLong, 1, TiffFieldValues.Long(image.Width)),
            (TiffConstants.TagImageLength, TiffConstants.TypeLong, 1, TiffFieldValues.Long(image.Height)),
            (TiffConstants.TagBitsPerSample, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts((ushort)image.BitsPerSample)),
            (TiffConstants.TagCompression, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts(TiffConstants.CompressionNone)),
            (TiffConstants.TagPhotometricInterpretation, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts(PhotometricBlackIsZero)),
            (TagStripOffsets, TiffConstants.TypeLong, 1, TiffFieldValues.Long(0)),
            (TiffConstants.TagSamplesPerPixel, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts(1)),
            (TagRowsPerStrip, TiffConstants.TypeLong, 1, TiffFieldValues.Long(image.Height)),
            (TagStripByteCounts, TiffConstants.TypeLong, 1, TiffFieldValues.Long(image.LittleEndianSamples.Length)),
            (TagPlanarConfiguration, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts(PlanarContiguous)),
            (TiffConstants.TagSampleFormat, TiffConstants.TypeShort, 1, TiffFieldValues.Shorts(image.SampleFormat)),
            (TiffConstants.TagModelPixelScaleTag, TiffConstants.TypeDouble, 3, TiffFieldValues.Doubles(
                (extent.XMax - extent.XMin) / image.Width,
                (extent.YMax - extent.YMin) / image.Height,
                0)),
            (TiffConstants.TagModelTiepointTag, TiffConstants.TypeDouble, 6, TiffFieldValues.Doubles(0, 0, 0, extent.XMin, extent.YMax, 0)),
        };

        if (CreateGeoKeys(extent.Srid ?? 0) is { } geoKeys)
        {
            fields.Add((TiffConstants.TagGeoKeyDirectoryTag, TiffConstants.TypeShort, (uint)(geoKeys.Length / 2), geoKeys));
        }

        if (image.NoData is not null)
        {
            var noData = Encoding.ASCII.GetBytes(image.NoData + "\0");
            fields.Add((TagGdalNoData, TiffConstants.TypeAscii, (uint)noData.Length, noData));
        }

        return fields;
    }

    // GeoKey directory: version 1.1.0 with three keys (model type, raster type, CRS code).
    // An SRID outside the 16-bit GeoKey range is left without a CRS key rather than truncated.
    private static byte[]? CreateGeoKeys(int srid)
    {
        if (srid is <= 0 or > ushort.MaxValue)
        {
            return null;
        }

        var geographic = GeographicSridClassifier.IsGeographicSrid(srid);
        return TiffFieldValues.Shorts(
            1, 1, 0, 3,
            GeoKeyModelType, 0, 1, geographic ? ModelTypeGeographic : ModelTypeProjected,
            TiffConstants.GeoKeyRasterTypeGeoKey, 0, 1, RasterPixelIsArea,
            geographic ? TiffConstants.GeoKeyGeographicTypeGeoKey : TiffConstants.GeoKeyProjectedCSTypeGeoKey, 0, 1, (ushort)srid);
    }
}
