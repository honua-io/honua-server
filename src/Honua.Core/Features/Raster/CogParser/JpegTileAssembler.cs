// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.CogParser;

/// <summary>
/// Turns a TIFF-JPEG tile (compression 7) into a standalone JPEG interchange stream.
/// libtiff and GDAL (default <c>JPEGTABLESMODE=1</c>) store the quantization tables, and
/// optionally the Huffman tables, once in the JPEGTables tag (347) and write every tile as an
/// abbreviated stream that references them (TIFF Technical Note 2). Such a tile is undecodable
/// on its own, so the tables are spliced in front of the tile's frame and the assembled stream
/// must define every table its frame and scan reference before it may be served as
/// <c>image/jpeg</c>.
/// </summary>
public static class JpegTileAssembler
{
    /// <summary>
    /// Upper bound for a JPEGTables payload. Four 16-bit quantization tables and eight
    /// Huffman tables fit in under 3 KiB, so anything larger is malformed.
    /// </summary>
    public const int MaxJpegTablesBytes = 64 * 1024;

    private const byte Soi = 0xD8;
    private const byte Eoi = 0xD9;
    private const byte Sof0 = 0xC0;
    private const byte Sof1 = 0xC1;
    private const byte Dht = 0xC4;
    private const byte Sos = 0xDA;
    private const byte Dqt = 0xDB;
    private const byte Dri = 0xDD;
    private const byte App0 = 0xE0;
    private const byte App14 = 0xEE;
    private const byte AppLast = 0xEF;
    private const byte Com = 0xFE;

    private const int PhotometricMinIsBlack = 1;
    private const int PhotometricRgb = 2;
    private const int PhotometricYCbCr = 6;

    // Adobe APP14, version 100, no flags, transform 0: the three components are RGB, not
    // YCbCr. libtiff suppresses JFIF/Adobe markers, so without it decoders that ignore the
    // 'R','G','B' component ids would colour-convert PHOTOMETRIC=RGB tiles as YCbCr.
    private static ReadOnlySpan<byte> AdobeRgbMarker =>
    [
        0xFF, App14, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e',
        0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00
    ];

    /// <summary>
    /// Returns true when <paramref name="tables"/> is a tables-only JPEG stream: SOI, then only
    /// quantization, Huffman, restart-interval, comment or application segments, then EOI.
    /// </summary>
    public static bool IsValidTables(ReadOnlySpan<byte> tables)
    {
        if (tables.Length is < 4 or > MaxJpegTablesBytes
            || tables[0] != 0xFF || tables[1] != Soi)
        {
            return false;
        }

        var tableSet = default(TableSet);
        var position = 2;
        while (true)
        {
            if (!TryReadMarker(tables, ref position, out var marker))
            {
                return false;
            }

            if (marker == Eoi)
            {
                return position == tables.Length;
            }

            if (!TryReadSegment(tables, ref position, out var segment))
            {
                return false;
            }

            switch (marker)
            {
                case Dqt when TryDefineQuantizationTables(segment, ref tableSet):
                case Dht when TryDefineHuffmanTables(segment, ref tableSet):
                case Dri or Com or (>= App0 and <= AppLast):
                    continue;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// Assembles a complete JPEG stream for one tile, or returns null when the tile cannot be
    /// served as a correctly decodable <c>image/jpeg</c> for the COG's declared layout.
    /// </summary>
    /// <param name="tile">Raw tile bytes as stored in the TIFF.</param>
    /// <param name="jpegTables">The tile's IFD JPEGTables payload, or null when the tiles are standalone.</param>
    /// <param name="metadata">Parsed COG metadata supplying the tile geometry and photometric layout.</param>
    public static byte[]? Assemble(ReadOnlySpan<byte> tile, byte[]? jpegTables, CogMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        if (!IsSupportedLayout(metadata)
            || tile.Length < 4
            || tile[0] != 0xFF || tile[1] != Soi
            || tile[^2] != 0xFF || tile[^1] != Eoi
            || (jpegTables is not null && !IsValidTables(jpegTables)))
        {
            return null;
        }

        // Tables stream minus its EOI, then the tile minus its SOI (libtiff's own splice).
        var tables = jpegTables is null ? ReadOnlySpan<byte>.Empty : jpegTables.AsSpan(2, jpegTables.Length - 4);
        var assembled = new byte[2 + tables.Length + tile.Length - 2];
        assembled[0] = 0xFF;
        assembled[1] = Soi;
        tables.CopyTo(assembled.AsSpan(2));
        tile[2..].CopyTo(assembled.AsSpan(2 + tables.Length));

        if (!TryValidateFrame(assembled, metadata, out var hasAdobeMarker))
        {
            return null;
        }

        if (metadata.PhotometricInterpretation != PhotometricRgb || hasAdobeMarker)
        {
            return assembled;
        }

        var withAdobe = new byte[assembled.Length + AdobeRgbMarker.Length];
        withAdobe[0] = 0xFF;
        withAdobe[1] = Soi;
        AdobeRgbMarker.CopyTo(withAdobe.AsSpan(2));
        assembled.AsSpan(2).CopyTo(withAdobe.AsSpan(2 + AdobeRgbMarker.Length));
        return withAdobe;
    }

    /// <summary>
    /// JPEG streams carry no photometric tag: decoders infer grayscale from one component and
    /// YCbCr from three unmarked components. Only layouts that inference (plus the injected
    /// Adobe marker for RGB) reproduces correctly are served.
    /// </summary>
    private static bool IsSupportedLayout(CogMetadata metadata)
        => metadata.PlanarConfiguration == 1
            && metadata.BitsPerSample == 8
            && ((metadata.BandCount == 1 && metadata.PhotometricInterpretation == PhotometricMinIsBlack)
                || (metadata.BandCount == 3
                    && metadata.PhotometricInterpretation is PhotometricRgb or PhotometricYCbCr));

    /// <summary>
    /// Walks the assembled stream up to its first scan. The frame must be baseline or extended
    /// sequential Huffman, 8-bit, tile-sized, with one component per band, and a single
    /// interleaved scan whose quantization and Huffman tables are all defined.
    /// </summary>
    private static bool TryValidateFrame(ReadOnlySpan<byte> stream, CogMetadata metadata, out bool hasAdobeMarker)
    {
        hasAdobeMarker = false;
        var tableSet = default(TableSet);
        Span<byte> componentIds = stackalloc byte[4];
        Span<byte> componentQuantTables = stackalloc byte[4];
        var componentCount = 0;
        var position = 2;

        while (true)
        {
            if (!TryReadMarker(stream, ref position, out var marker)
                || marker is Soi or Eoi or 0x01 or (>= 0xD0 and <= 0xD7)
                || !TryReadSegment(stream, ref position, out var segment))
            {
                return false;
            }

            switch (marker)
            {
                case Dqt:
                    if (!TryDefineQuantizationTables(segment, ref tableSet))
                    {
                        return false;
                    }
                    break;
                case Dht:
                    if (!TryDefineHuffmanTables(segment, ref tableSet))
                    {
                        return false;
                    }
                    break;
                case Sof0 or Sof1:
                    if (componentCount != 0 || segment.Length < 6)
                    {
                        return false;
                    }
                    componentCount = segment[5];
                    if (segment[0] != 8
                        || BinaryPrimitives.ReadUInt16BigEndian(segment[1..]) != metadata.TileHeight
                        || BinaryPrimitives.ReadUInt16BigEndian(segment[3..]) != metadata.TileWidth
                        || componentCount != metadata.BandCount
                        || segment.Length != 6 + (3 * componentCount))
                    {
                        return false;
                    }
                    for (var i = 0; i < componentCount; i++)
                    {
                        componentIds[i] = segment[6 + (3 * i)];
                        componentQuantTables[i] = segment[8 + (3 * i)];
                    }
                    break;
                case Sos:
                    return componentCount != 0
                        && TryValidateScan(segment, componentIds[..componentCount], componentQuantTables[..componentCount], tableSet);
                case >= 0xC0 and <= 0xCF:
                    // Progressive, lossless, hierarchical and arithmetic-coded frames (and DAC).
                    return false;
                case App14:
                    hasAdobeMarker |= segment.StartsWith("Adobe"u8);
                    break;
                default:
                    // DRI, APPn, COM and other parameter segments do not affect table coverage.
                    break;
            }
        }
    }

    private static bool TryValidateScan(
        ReadOnlySpan<byte> segment,
        ReadOnlySpan<byte> componentIds,
        ReadOnlySpan<byte> componentQuantTables,
        TableSet tableSet)
    {
        foreach (var quantTable in componentQuantTables)
        {
            if (quantTable > 3 || (tableSet.Quantization & (1 << quantTable)) == 0)
            {
                return false;
            }
        }

        if (segment.Length < 1 || segment[0] != componentIds.Length || segment.Length != 4 + (2 * segment[0]))
        {
            return false;
        }

        for (var i = 0; i < componentIds.Length; i++)
        {
            var dcTable = segment[2 + (2 * i)] >> 4;
            var acTable = segment[2 + (2 * i)] & 0x0F;
            if (componentIds.IndexOf(segment[1 + (2 * i)]) < 0
                || dcTable > 3 || acTable > 3
                || (tableSet.HuffmanDc & (1 << dcTable)) == 0
                || (tableSet.HuffmanAc & (1 << acTable)) == 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryDefineQuantizationTables(ReadOnlySpan<byte> segment, ref TableSet tableSet)
    {
        if (segment.IsEmpty)
        {
            return false;
        }

        var offset = 0;
        while (offset < segment.Length)
        {
            var precision = segment[offset] >> 4;
            var id = segment[offset] & 0x0F;
            var size = 1 + (64 * (precision + 1));
            if (precision > 1 || id > 3 || offset + size > segment.Length)
            {
                return false;
            }
            tableSet.Quantization |= 1 << id;
            offset += size;
        }

        return true;
    }

    private static bool TryDefineHuffmanTables(ReadOnlySpan<byte> segment, ref TableSet tableSet)
    {
        if (segment.IsEmpty)
        {
            return false;
        }

        var offset = 0;
        while (offset < segment.Length)
        {
            if (offset + 17 > segment.Length)
            {
                return false;
            }
            var tableClass = segment[offset] >> 4;
            var id = segment[offset] & 0x0F;
            var symbolCount = 0;
            foreach (var count in segment.Slice(offset + 1, 16))
            {
                symbolCount += count;
            }
            if (tableClass > 1 || id > 3 || symbolCount > 256 || offset + 17 + symbolCount > segment.Length)
            {
                return false;
            }
            if (tableClass == 0)
            {
                tableSet.HuffmanDc |= 1 << id;
            }
            else
            {
                tableSet.HuffmanAc |= 1 << id;
            }
            offset += 17 + symbolCount;
        }

        return true;
    }

    private static bool TryReadMarker(ReadOnlySpan<byte> stream, ref int position, out byte marker)
    {
        marker = 0;
        if (position >= stream.Length || stream[position] != 0xFF)
        {
            return false;
        }

        // Any number of 0xFF fill bytes may precede a marker code.
        while (position < stream.Length && stream[position] == 0xFF)
        {
            position++;
        }

        if (position >= stream.Length || stream[position] == 0x00)
        {
            return false;
        }

        marker = stream[position++];
        return true;
    }

    private static bool TryReadSegment(ReadOnlySpan<byte> stream, ref int position, out ReadOnlySpan<byte> segment)
    {
        segment = default;
        if (position + 2 > stream.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt16BigEndian(stream[position..]);
        if (length < 2 || position + length > stream.Length)
        {
            return false;
        }

        segment = stream.Slice(position + 2, length - 2);
        position += length;
        return true;
    }

    private struct TableSet
    {
        public int Quantization;
        public int HuffmanDc;
        public int HuffmanAc;
    }
}
