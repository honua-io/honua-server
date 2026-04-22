// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Raster.CogParser;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Unit tests for the AOT-safe TIFF IFD parser.
/// </summary>
public class TiffIfdParserTests
{
    [Fact]
    public void ParseHeader_LittleEndianClassicTiff_ReturnsCorrectOffset()
    {
        // Arrange — classic TIFF, little-endian, first IFD at offset 8
        byte[] header = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];

        // Act
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(header);

        // Assert
        parser.IsLittleEndian.Should().BeTrue();
        parser.IsBigTiff.Should().BeFalse();
        firstIfdOffset.Should().Be(8);
    }

    [Fact]
    public void ParseHeader_BigEndianClassicTiff_ReturnsCorrectOffset()
    {
        // Arrange — classic TIFF, big-endian, first IFD at offset 8
        byte[] header = [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08];

        // Act
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(header);

        // Assert
        parser.IsLittleEndian.Should().BeFalse();
        parser.IsBigTiff.Should().BeFalse();
        firstIfdOffset.Should().Be(8);
    }

    [Fact]
    public void ParseHeader_BigTiffLittleEndian_ReturnsCorrectProperties()
    {
        // Arrange — BigTIFF, little-endian, first IFD at offset 16
        byte[] header =
        [
            0x49, 0x49,             // "II"
            0x2B, 0x00,             // magic 43 (BigTIFF)
            0x08, 0x00,             // offset byte size = 8
            0x00, 0x00,             // always 0
            0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00  // first IFD at 16
        ];

        // Act
        var (parser, firstIfdOffset) = TiffIfdParser.ParseHeader(header);

        // Assert
        parser.IsLittleEndian.Should().BeTrue();
        parser.IsBigTiff.Should().BeTrue();
        firstIfdOffset.Should().Be(16);
    }

    [Fact]
    public void ParseHeader_InvalidByteOrder_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] header = [0x00, 0x00, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];

        // Act & Assert
        var act = () => TiffIfdParser.ParseHeader(header);
        act.Should().Throw<InvalidDataException>().WithMessage("*byte order*");
    }

    [Fact]
    public void ParseHeader_InvalidMagic_ThrowsInvalidDataException()
    {
        // Arrange — valid byte order but wrong magic number
        byte[] header = [0x49, 0x49, 0x99, 0x00, 0x08, 0x00, 0x00, 0x00];

        // Act & Assert
        var act = () => TiffIfdParser.ParseHeader(header);
        act.Should().Throw<InvalidDataException>().WithMessage("*magic*");
    }

    [Fact]
    public void ParseHeader_TooSmall_ThrowsInvalidDataException()
    {
        // Arrange
        byte[] header = [0x49, 0x49];

        // Act & Assert
        var act = () => TiffIfdParser.ParseHeader(header);
        act.Should().Throw<InvalidDataException>().WithMessage("*too small*");
    }

    [Fact]
    public void ParseIfd_ClassicTiffWithOneEntry_ReturnsEntry()
    {
        // Arrange — header for a little-endian classic TIFF
        byte[] header = [0x49, 0x49, 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00];
        var (parser, _) = TiffIfdParser.ParseHeader(header);

        // Construct an IFD with 1 entry: tag=256 (ImageWidth), type=LONG, count=1, value=1024
        byte[] ifdData =
        [
            0x01, 0x00,                                     // entry count = 1
            0x00, 0x01,                                     // tag = 256 (ImageWidth)
            0x04, 0x00,                                     // type = LONG (4)
            0x01, 0x00, 0x00, 0x00,                         // count = 1
            0x00, 0x04, 0x00, 0x00,                         // value = 1024
            0x00, 0x00, 0x00, 0x00                          // next IFD offset = 0
        ];

        // Act
        var (entries, nextIfdOffset) = parser.ParseIfd(ifdData);

        // Assert
        entries.Should().HaveCount(1);
        entries[0].Tag.Should().Be(256);
        entries[0].Type.Should().Be(4); // LONG
        entries[0].Count.Should().Be(1);
        entries[0].ValueOrOffset.Should().Be(1024);
        nextIfdOffset.Should().Be(0);
    }

    [Fact]
    public void ParseIfd_BigEndianInlineShort_ReadsValueCorrectly()
    {
        // Arrange — big-endian classic TIFF
        byte[] header = [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08];
        var (parser, _) = TiffIfdParser.ParseHeader(header);

        // Construct an IFD with 1 entry: tag=259 (Compression), type=SHORT, count=1, value=7 (JPEG)
        // Big-endian: SHORT value 7 left-justified in 4-byte value field = [0x00, 0x07, 0x00, 0x00]
        byte[] ifdData =
        [
            0x00, 0x01,                         // entry count = 1
            0x01, 0x03,                         // tag = 259 (Compression)
            0x00, 0x03,                         // type = SHORT (3)
            0x00, 0x00, 0x00, 0x01,             // count = 1
            0x00, 0x07, 0x00, 0x00,             // value = 7 (JPEG), left-justified
            0x00, 0x00, 0x00, 0x00              // next IFD offset = 0
        ];

        // Act
        var (entries, _) = parser.ParseIfd(ifdData);

        // Assert — value must be 7, not 0x00070000
        entries.Should().HaveCount(1);
        entries[0].Tag.Should().Be(259);
        entries[0].Type.Should().Be(3); // SHORT
        entries[0].ValueOrOffset.Should().Be(7);
        entries[0].IsInline.Should().BeTrue();
    }

    [Fact]
    public void GetCompressionName_KnownValues_ReturnsCorrectNames()
    {
        TiffConstants.GetCompressionName(1).Should().Be("NONE");
        TiffConstants.GetCompressionName(7).Should().Be("JPEG");
        TiffConstants.GetCompressionName(8).Should().Be("DEFLATE");
        TiffConstants.GetCompressionName(32946).Should().Be("DEFLATE");
        TiffConstants.GetCompressionName(5).Should().Be("LZW");
        TiffConstants.GetCompressionName(50000).Should().Be("ZSTD");
    }

    [Fact]
    public void GetTypeSize_AllTypes_ReturnsCorrectSizes()
    {
        TiffConstants.GetTypeSize(TiffConstants.TypeByte).Should().Be(1);
        TiffConstants.GetTypeSize(TiffConstants.TypeShort).Should().Be(2);
        TiffConstants.GetTypeSize(TiffConstants.TypeLong).Should().Be(4);
        TiffConstants.GetTypeSize(TiffConstants.TypeDouble).Should().Be(8);
        TiffConstants.GetTypeSize(TiffConstants.TypeLong8).Should().Be(8);
    }

    [Fact]
    public void TileDecompressor_IsSupported_ReturnsCorrectResults()
    {
        TileDecompressor.IsSupported("JPEG").Should().BeTrue();
        TileDecompressor.IsSupported("DEFLATE").Should().BeTrue();
        TileDecompressor.IsSupported("NONE").Should().BeTrue();
        TileDecompressor.IsSupported("").Should().BeTrue();
        TileDecompressor.IsSupported("LZW").Should().BeFalse();
        TileDecompressor.IsSupported("ZSTD").Should().BeFalse();
    }

    [Fact]
    public void TileDecompressor_JpegPassthrough_ReturnsSameData()
    {
        // Arrange
        byte[] jpegData = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10];

        // Act
        var (data, contentType) = TileDecompressor.Decompress(jpegData, "JPEG");

        // Assert — zero-copy passthrough
        data.Should().BeSameAs(jpegData);
        contentType.Should().Be("image/jpeg");
    }

    [Fact]
    public void TileDecompressor_None_ReturnsOctetStreamContentType()
    {
        // Arrange — raw pixel data (no compression)
        byte[] rawData = [0x01, 0x02, 0x03, 0x04];

        // Act
        var (data, contentType) = TileDecompressor.Decompress(rawData, "NONE");

        // Assert — raw pixels are not a renderable image format
        data.Should().BeSameAs(rawData);
        contentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public void TileDecompressor_Deflate_ReturnsOctetStreamContentType()
    {
        // Arrange — create zlib-compressed data
        using var ms = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionMode.Compress, leaveOpen: true))
        {
            zlib.Write([0x01, 0x02, 0x03, 0x04]);
        }
        var zlibData = ms.ToArray();

        // Act
        var (data, contentType) = TileDecompressor.Decompress(zlibData, "DEFLATE");

        // Assert — decompressed raw pixels, not a renderable image
        data.Should().Equal(0x01, 0x02, 0x03, 0x04);
        contentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public void TileDecompressor_EmptyString_TreatedAsNone()
    {
        // Arrange — empty compression string (DB default when column is NULL)
        byte[] rawData = [0x01, 0x02, 0x03, 0x04];

        // Act
        var (data, contentType) = TileDecompressor.Decompress(rawData, "");

        // Assert — same behavior as NONE: raw passthrough
        data.Should().BeSameAs(rawData);
        contentType.Should().Be("application/octet-stream");
    }

    [Fact]
    public void TileDecompressor_UnsupportedCompression_ThrowsNotSupportedException()
    {
        // Arrange
        byte[] data = [0x01, 0x02, 0x03];

        // Act & Assert
        var act = () => TileDecompressor.Decompress(data, "LZW");
        act.Should().Throw<NotSupportedException>().WithMessage("*LZW*");
    }
}
