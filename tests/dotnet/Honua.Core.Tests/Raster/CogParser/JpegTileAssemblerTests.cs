// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Raster.CogParser;
using Honua.Core.Features.Raster.Domain;
using Xunit;

namespace Honua.Core.Tests.Raster.CogParser;

/// <summary>
/// Structural proof for JPEGTables assembly (#4205) against GDAL-written TIFF-JPEG tiles.
///
/// The <c>jpeg_*</c> fixtures were written by GDAL 3.13.1 with its default
/// <c>JPEGTABLESMODE=1</c> (<c>scripts/raster/generate-jpeg-cog-fixtures.py</c>): the
/// quantization tables live only in tag 347 and every tile is an abbreviated stream. The
/// pixel-level oracle (GDAL's own decode, compared after a real JPEG decoder reads the
/// assembled stream) is in the server COG tests, which carry a JPEG decoder.
/// </summary>
public sealed class JpegTileAssemblerTests
{
    private static readonly string FixtureDirectory =
        Path.Join(AppContext.BaseDirectory, "Raster", "CogParser", "Fixtures");

    private static readonly byte[] AdobeRgbMarker =
        [0xFF, 0xEE, 0x00, 0x0E, (byte)'A', (byte)'d', (byte)'o', (byte)'b', (byte)'e', 0x00, 0x64, 0x00, 0x00, 0x00, 0x00, 0x00];

    public static TheoryData<string, int, int, int> GdalJpegFixtures() => new()
    {
        // name, bands, photometric, JPEGTables length written by GDAL (two DQTs for YCbCr)
        { "jpeg_ycbcr_rgb_uint8", 3, 6, 142 },
        { "jpeg_gray_uint8", 1, 1, 73 },
        { "jpeg_rgb_uint8", 3, 2, 73 },
    };

    [Theory]
    [MemberData(nameof(GdalJpegFixtures))]
    public async Task ReadMetadata_GdalJpegCog_CarriesSharedTablesForAbbreviatedTiles(
        string fixture, int bands, int photometric, int tablesLength)
    {
        var (metadata, tile) = await ReadFirstTileAsync(fixture);

        metadata.Compression.Should().Be("JPEG");
        metadata.BandCount.Should().Be(bands);
        metadata.PhotometricInterpretation.Should().Be(photometric);
        var tables = metadata.OverviewLevels[0].JpegTables;
        tables.Should().NotBeNull().And.HaveCount(tablesLength);
        tables![..4].Should().Equal(0xFF, 0xD8, 0xFF, 0xDB);
        tables[^2..].Should().Equal(0xFF, 0xD9);

        // The defect's precondition: GDAL's tile defines no quantization table of its own,
        // so serving it verbatim is undecodable.
        IndexOf(tile, [0xFF, 0xDB]).Should().Be(-1);
    }

    [Theory]
    [MemberData(nameof(GdalJpegFixtures))]
    public async Task Assemble_GdalJpegTile_SplicesTablesBeforeTheFrame(
        string fixture, int bands, int photometric, int tablesLength)
    {
        _ = bands;
        var (metadata, tile) = await ReadFirstTileAsync(fixture);
        var tables = metadata.OverviewLevels[0].JpegTables!;

        var assembled = JpegTileAssembler.Assemble(tile, tables, metadata);

        assembled.Should().NotBeNull();
        var adobe = photometric == 2 ? AdobeRgbMarker : [];
        assembled!.Length.Should().Be(tablesLength - 2 + adobe.Length + tile.Length - 2);
        assembled[..2].Should().Equal(0xFF, 0xD8);
        assembled[2..(2 + adobe.Length)].Should().Equal(adobe);
        assembled[(2 + adobe.Length)..(adobe.Length + tablesLength - 2)].Should().Equal(tables[2..^2]);
        assembled[(adobe.Length + tablesLength - 2)..].Should().Equal(tile[2..]);
        IndexOf(assembled, [0xFF, 0xDB]).Should().BeLessThan(IndexOf(assembled, [0xFF, 0xC0]));
    }

    [Fact]
    public async Task Assemble_AbbreviatedTileWithoutItsTables_ReturnsNull()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_gray_uint8");

        JpegTileAssembler.Assemble(tile, jpegTables: null, metadata).Should().BeNull();
    }

    [Fact]
    public async Task Assemble_TablesDefiningAnotherQuantizationTable_ReturnsNull()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_gray_uint8");
        var tables = metadata.OverviewLevels[0].JpegTables!.ToArray();
        tables[6].Should().Be(0x00, "the fixture's only DQT defines 8-bit table 0");
        tables[6] = 0x01;

        JpegTileAssembler.IsValidTables(tables).Should().BeTrue();
        JpegTileAssembler.Assemble(tile, tables, metadata).Should().BeNull();
    }

    [Fact]
    public async Task Assemble_TablesWithoutHuffmanForAnAbbreviatedScan_ReturnsNull()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_gray_uint8");
        var tables = metadata.OverviewLevels[0].JpegTables!;
        var dht = IndexOf(tile, [0xFF, 0xC4]);
        var sos = IndexOf(tile, [0xFF, 0xDA]);
        // Drop the tile's own DHT segments: the scan then references undefined Huffman tables.
        var withoutHuffman = tile[..dht].Concat(tile[sos..]).ToArray();

        JpegTileAssembler.Assemble(withoutHuffman, tables, metadata).Should().BeNull();
    }

    [Theory]
    [InlineData(4, 2, 1, 8)] // Four components would decode as CMYK/YCCK.
    [InlineData(3, 5, 1, 8)] // Separated (CMYK) photometric.
    [InlineData(1, 0, 1, 8)] // WhiteIsZero would render inverted.
    [InlineData(3, 6, 2, 8)] // Separate planes.
    [InlineData(3, 6, 1, 12)] // 12-bit JPEG is not a browser/Pro-decodable image/jpeg.
    public async Task Assemble_LayoutJpegDecodersCannotInfer_ReturnsNull(
        int bands, int photometric, int planar, int bitsPerSample)
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_ycbcr_rgb_uint8");
        var declared = metadata with
        {
            BandCount = bands,
            PhotometricInterpretation = photometric,
            PlanarConfiguration = planar,
            BitsPerSample = bitsPerSample
        };

        JpegTileAssembler.Assemble(tile, metadata.OverviewLevels[0].JpegTables, declared).Should().BeNull();
    }

    [Fact]
    public async Task Assemble_FrameDisagreeingWithTileGeometryOrBands_ReturnsNull()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_ycbcr_rgb_uint8");
        var tables = metadata.OverviewLevels[0].JpegTables;

        JpegTileAssembler.Assemble(tile, tables, metadata with { TileWidth = 256 }).Should().BeNull();
        JpegTileAssembler.Assemble(tile, tables, metadata with { BandCount = 1, PhotometricInterpretation = 1 })
            .Should().BeNull();
    }

    [Fact]
    public async Task Assemble_ProgressiveOrTruncatedTile_ReturnsNull()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_gray_uint8");
        var tables = metadata.OverviewLevels[0].JpegTables;
        var progressive = tile.ToArray();
        progressive[IndexOf(tile, [0xFF, 0xC0]) + 1] = 0xC2;

        JpegTileAssembler.Assemble(progressive, tables, metadata).Should().BeNull();
        JpegTileAssembler.Assemble(tile.AsSpan(..^2), tables, metadata).Should().BeNull();
        JpegTileAssembler.Assemble(tile.AsSpan(1..), tables, metadata).Should().BeNull();
    }

    [Fact]
    public async Task IsValidTables_RejectsStreamsThatAreNotTablesOnly()
    {
        var (metadata, tile) = await ReadFirstTileAsync("jpeg_gray_uint8");
        var tables = metadata.OverviewLevels[0].JpegTables!;

        JpegTileAssembler.IsValidTables(tables).Should().BeTrue();
        JpegTileAssembler.IsValidTables([0xFF, 0xD8, 0xFF, 0xD9]).Should().BeTrue();
        JpegTileAssembler.IsValidTables(tables.AsSpan(..^2)).Should().BeFalse("EOI is missing");
        JpegTileAssembler.IsValidTables([.. tables, 0x00]).Should().BeFalse("bytes trail the EOI");
        JpegTileAssembler.IsValidTables(tables[..^3].Concat(tables[^2..]).ToArray())
            .Should().BeFalse("the DQT segment is truncated");
        var withFrame = tables[..^2].Concat(tile[2..IndexOf(tile, [0xFF, 0xC4])]).Concat(tables[^2..]).ToArray();
        JpegTileAssembler.IsValidTables(withFrame).Should().BeFalse("a frame header is not a table");
        JpegTileAssembler.IsValidTables(new byte[JpegTileAssembler.MaxJpegTablesBytes + 1]).Should().BeFalse();
    }

    private static async Task<(CogMetadata Metadata, byte[] Tile)> ReadFirstTileAsync(string fixture)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Join(FixtureDirectory, fixture + ".tif"));
        var metadata = await new CogMetadataExtractor().ReadMetadataAsync(
            new InMemoryRangeReader(bytes), "fixtures", fixture + ".tif");
        var level = metadata.OverviewLevels[0];
        var tile = bytes.AsSpan((int)level.TileOffsets[0], level.TileByteCounts[0]).ToArray();
        return (metadata, tile);
    }

    private static int IndexOf(byte[] data, byte[] pattern) => data.AsSpan().IndexOf(pattern);

    private sealed class InMemoryRangeReader(byte[] data) : ICloudRangeReader
    {
        public CloudStorageProvider Provider => CloudStorageProvider.AwsS3;

        public Task<byte[]> ReadRangeAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, data.Length - (int)offset);
            return Task.FromResult(data.AsSpan((int)Math.Min(offset, data.Length), Math.Min(length, available)).ToArray());
        }

        public Task<Stream> ReadRangeStreamAsync(string bucket, string key, long offset, int length, CancellationToken cancellationToken = default)
        {
            var available = Math.Max(0, data.Length - (int)offset);
            return Task.FromResult<Stream>(new Honua.TestKit.CallerOwnedMemoryStream(data, (int)offset, Math.Min(length, available)));
        }

        public Task<long> GetObjectSizeAsync(string bucket, string key, CancellationToken cancellationToken = default)
            => Task.FromResult((long)data.Length);
    }
}
