// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// TIFF/BigTIFF constants for COG IFD parsing.
/// Uses only primitive types and constants — no reflection, fully AOT-safe.
/// </summary>
internal static class TiffConstants
{
    // Byte order markers
    public const ushort LittleEndianMarker = 0x4949; // "II"
    public const ushort BigEndianMarker = 0x4D4D;    // "MM"

    // TIFF version markers (following byte order)
    public const ushort ClassicTiffMagic = 42;
    public const ushort BigTiffMagic = 43;

    // Classic TIFF header sizes
    public const int ClassicHeaderSize = 8;
    public const int ClassicIfdEntrySize = 12;

    // BigTIFF header sizes
    public const int BigTiffHeaderSize = 16;
    public const int BigTiffIfdEntrySize = 20;

    // Tag IDs relevant to COG parsing
    public const ushort TagImageWidth = 256;
    public const ushort TagImageLength = 257;
    public const ushort TagBitsPerSample = 258;
    public const ushort TagCompression = 259;
    public const ushort TagPhotometricInterpretation = 262;
    public const ushort TagSamplesPerPixel = 277;
    public const ushort TagTileWidth = 322;
    public const ushort TagTileLength = 323;
    public const ushort TagTileOffsets = 324;
    public const ushort TagTileByteCounts = 325;
    public const ushort TagSampleFormat = 339;

    // SampleFormat values (TIFF tag 339)
    public const ushort SampleFormatUnsigned = 1;
    public const ushort SampleFormatSigned = 2;
    public const ushort SampleFormatFloat = 3;

    // GeoTIFF tag IDs
    public const ushort TagModelTiepointTag = 33922;
    public const ushort TagModelPixelScaleTag = 33550;
    public const ushort TagGeoKeyDirectoryTag = 34735;

    // GeoKey IDs within GeoKeyDirectoryTag
    public const ushort GeoKeyProjectedCSTypeGeoKey = 3072;
    public const ushort GeoKeyGeographicTypeGeoKey = 2048;

    // TIFF field types
    public const ushort TypeByte = 1;
    public const ushort TypeAscii = 2;
    public const ushort TypeShort = 3;
    public const ushort TypeLong = 4;
    public const ushort TypeRational = 5;
    public const ushort TypeSByte = 6;
    public const ushort TypeUndefined = 7;
    public const ushort TypeSShort = 8;
    public const ushort TypeSLong = 9;
    public const ushort TypeSRational = 10;
    public const ushort TypeFloat = 11;
    public const ushort TypeDouble = 12;
    public const ushort TypeLong8 = 16;   // BigTIFF
    public const ushort TypeSLong8 = 17;  // BigTIFF

    // Compression values
    public const ushort CompressionNone = 1;
    public const ushort CompressionJpeg = 7;
    public const ushort CompressionDeflate = 8;
    public const ushort CompressionAdobeDeflate = 32946;
    public const ushort CompressionLzw = 5;
    public const ushort CompressionZstd = 50000;

    /// <summary>
    /// Gets the size in bytes of a TIFF field type value.
    /// </summary>
    public static int GetTypeSize(ushort type) => type switch
    {
        TypeByte or TypeAscii or TypeSByte or TypeUndefined => 1,
        TypeShort or TypeSShort => 2,
        TypeLong or TypeSLong or TypeFloat => 4,
        TypeRational or TypeSRational or TypeDouble or TypeLong8 or TypeSLong8 => 8,
        _ => 1
    };

    /// <summary>
    /// Maps a TIFF compression code to a human-readable string.
    /// </summary>
    public static string GetCompressionName(ushort compression) => compression switch
    {
        CompressionNone => "NONE",
        CompressionJpeg => "JPEG",
        CompressionDeflate or CompressionAdobeDeflate => "DEFLATE",
        CompressionLzw => "LZW",
        CompressionZstd => "ZSTD",
        _ => $"UNKNOWN({compression})"
    };
}
