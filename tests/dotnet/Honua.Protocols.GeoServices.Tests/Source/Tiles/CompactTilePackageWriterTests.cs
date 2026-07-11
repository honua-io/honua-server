// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Tiles;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Tiles;

/// <summary>
/// Compatibility tests for the Esri TPKX 1.0 / Compact Cache V2 writer.
/// Specification sources: https://github.com/Esri/tile-package-spec and
/// https://github.com/Esri/raster-tiles-compactcache.
/// </summary>
[Protocol(TestProtocols.MapServer)]
public sealed class CompactTilePackageWriterTests
{
    private const int BundleDataOffset = 131_136;
    private const string EsriRootSampleShape =
        """{"minScale":591657527.591555,"maxScale":9027.977411,"resampling":true,"tileImageInfo":{"format":"PNG"}}""";
    private const string EsriItemInfoSampleShape =
        """{"version":1.0,"extent":{"xmin":-178.19824218748033,"ymin":18.937464429663208,"xmax":-66.972656250009919,"ymax":71.413176833965693,"spatialReference":{"wkid":4326,"latestWkid":4326}}}""";

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_ProducesDeterministicTpkxLayoutAndReadableCompactV2Bundles()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01, 0x02, 0x03]),
            new TilePackageWriter.PackagedTile(8, 128, 0, [0x10, 0x11]),
            new TilePackageWriter.PackagedTile(8, 130, 129, [0x20, 0x21, 0x22, 0x23]),
        };

        var first = await WritePackageAsync(tiles);
        var second = await WritePackageAsync(tiles);

        first.Should().Equal(second, "identical tiles must produce byte-for-byte deterministic packages");

        using var archive = new ZipArchive(new MemoryStream(first), ZipArchiveMode.Read);
        archive.Entries.Select(static entry => entry.FullName).Should().BeEquivalentTo(
            [
                "tile/L00/R0000C0000.bundle",
                "tile/L08/R0000C0080.bundle",
                "tile/L08/R0080C0080.bundle",
                "root.json",
                "iteminfo.json",
                "thumbnail.png",
            ]);

        using (var root = await ReadJsonAsync(archive, "root.json"))
        using (var esriRootSample = JsonDocument.Parse(EsriRootSampleShape))
        {
            root.RootElement.GetProperty("version").GetDouble().Should().Be(1d);
            root.RootElement.GetProperty("tileBundlesPath").GetString().Should().Be("./tile");
            root.RootElement.GetProperty("minLOD").GetInt32().Should().Be(0);
            root.RootElement.GetProperty("maxLOD").GetInt32().Should().Be(8);
            root.RootElement.GetProperty("minScale").GetDouble().Should()
                .BeGreaterThan(root.RootElement.GetProperty("maxScale").GetDouble());
            root.RootElement.GetProperty("resampling").ValueKind.Should().Be(JsonValueKind.True);
            root.RootElement.GetProperty("resampling").ValueKind.Should()
                .Be(esriRootSample.RootElement.GetProperty("resampling").ValueKind);
            root.RootElement.GetProperty("tileImageInfo").GetProperty("format").GetString().Should().Be("PNG");
            root.RootElement.GetProperty("storageInfo").GetProperty("storageFormat").GetString()
                .Should().Be("esriMapCacheStorageModeCompactV2");
            root.RootElement.GetProperty("storageInfo").GetProperty("packetSize").GetInt32().Should().Be(128);
        }

        using (var itemInfo = await ReadJsonAsync(archive, "iteminfo.json"))
        using (var esriItemInfoSample = JsonDocument.Parse(EsriItemInfoSampleShape))
        {
            itemInfo.RootElement.GetProperty("version").ValueKind.Should()
                .Be(esriItemInfoSample.RootElement.GetProperty("version").ValueKind);
            itemInfo.RootElement.GetProperty("type").GetString().Should().Be("Compact Tile Package");
            itemInfo.RootElement.GetProperty("typeKeywords").EnumerateArray().Select(static value => value.GetString())
                .Should().Contain(["Compact Tile Package", "Tile Package", "tpkx"]);
            var extent = itemInfo.RootElement.GetProperty("extent");
            extent.ValueKind.Should().Be(esriItemInfoSample.RootElement.GetProperty("extent").ValueKind);
            extent.GetProperty("xmin").GetDouble().Should().Be(-180d);
            extent.GetProperty("ymin").GetDouble().Should().Be(-85d);
            extent.GetProperty("xmax").GetDouble().Should().Be(180d);
            extent.GetProperty("ymax").GetDouble().Should().Be(85d);
            extent.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            extent.GetProperty("spatialReference").ValueKind.Should()
                .Be(esriItemInfoSample.RootElement.GetProperty("extent").GetProperty("spatialReference").ValueKind);
            itemInfo.RootElement.TryGetProperty("spatialReference", out _).Should().BeFalse();
        }

        var levelZeroBundle = await ReadEntryAsync(archive, "tile/L00/R0000C0000.bundle");
        AssertBundleHeader(levelZeroBundle, maximumTileSize: 3);
        BinaryPrimitives.ReadUInt64LittleEndian(levelZeroBundle.AsSpan(64, sizeof(ulong))).Should()
            .Be(0x0000030000020044UL, "Esri CompactV2 stores a 24-bit size above the 40-bit data offset");
        BinaryPrimitives.ReadUInt32LittleEndian(levelZeroBundle.AsSpan(BundleDataOffset, sizeof(uint))).Should().Be(3);
        levelZeroBundle.AsSpan(BundleDataOffset + sizeof(uint), 3).ToArray().Should().Equal(0x01, 0x02, 0x03);
        ReadTile(levelZeroBundle, relativeRow: 0, relativeColumn: 0).Should().Equal(0x01, 0x02, 0x03);
        ReadTile(levelZeroBundle, relativeRow: 0, relativeColumn: 1).Should().BeEmpty();

        var secondBundle = await ReadEntryAsync(archive, "tile/L08/R0080C0080.bundle");
        AssertBundleHeader(secondBundle, maximumTileSize: 4);
        ReadTile(secondBundle, relativeRow: 1, relativeColumn: 2).Should().Equal(0x20, 0x21, 0x22, 0x23);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_OutOfBundleOrder_RejectsAmbiguousStreamingInput()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(8, 128, 0, [0x01]),
            new TilePackageWriter.PackagedTile(8, 0, 0, [0x02]),
        };
        using var stream = new MemoryStream();

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bundle order*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_DuplicateTile_RejectsAmbiguousBundleIndex()
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(3, 4, 5, [0x01]),
            new TilePackageWriter.PackagedTile(3, 4, 5, [0x02]),
        };
        using var stream = new MemoryStream();

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*same tile more than once*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_EmptyInput_RejectsPackageWithoutUsableLodRange()
    {
        using var stream = new MemoryStream();

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync([]),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one tile*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_SingleLevel_RejectsInvalidLodRange()
    {
        using var stream = new MemoryStream();
        var tiles = new[] { new TilePackageWriter.PackagedTile(2, 0, 0, [0x01]) };

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least two levels*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_EmptyOrUnsupportedTileFormat_RejectsBeforeReadingTiles()
    {
        foreach (var tileFormat in new[] { string.Empty, "WEBP" })
        {
            using var stream = new MemoryStream();

            var act = () => CompactTilePackageWriter.WriteAsync(
                stream,
                "Layers",
                tileFormat,
                [-180d, -85d, 180d, 85d],
                ToAsync([]),
                CancellationToken.None);

            await act.Should().ThrowAsync<ArgumentException>()
                .WithMessage("*supported TPKX tile format*");
            stream.Length.Should().Be(0, "format admission must happen before the archive is opened");
        }
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Operation(Operations.Export)]
    [InlineData("png", "PNG")]
    [InlineData("PNG8", "PNG8")]
    [InlineData("PNG24", "PNG24")]
    [InlineData("PNG32", "PNG32")]
    [InlineData("jpeg", "JPEG")]
    [InlineData("MIXED", "MIXED")]
    public async Task WriteAsync_SupportedTileFormat_EmitsDocumentedValue(string tileFormat, string expected)
    {
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01]),
            new TilePackageWriter.PackagedTile(1, 0, 0, [0x02]),
        };
        using var stream = new MemoryStream();
        await CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            tileFormat,
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);

        using var archive = new ZipArchive(new MemoryStream(stream.ToArray()), ZipArchiveMode.Read);
        using var root = await ReadJsonAsync(archive, "root.json");
        root.RootElement.GetProperty("tileImageInfo").GetProperty("format").GetString().Should().Be(expected);
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_BundleAdmissionLimit_RejectsBeforeGrowingBuffer()
    {
        using var stream = new MemoryStream();
        var tiles = new[] { new TilePackageWriter.PackagedTile(0, 0, 0, [0x01, 0x02, 0x03, 0x04]) };
        var limits = new CompactTilePackageLimits(BundleDataOffset + 7, BundleDataOffset * 2L);

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None,
            limits);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bundle admission limit*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_PackageAdmissionLimit_RejectsAdditionalBundle()
    {
        using var stream = new MemoryStream();
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01]),
            new TilePackageWriter.PackagedTile(1, 0, 0, [0x02]),
        };
        var limits = new CompactTilePackageLimits(BundleDataOffset + 5, (BundleDataOffset * 2L) + 9);

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None,
            limits);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*package admission limit*");
    }

    [UnitTest]
    [Operation(Operations.Export)]
    public async Task WriteAsync_SparseBundles_ChargesFixedHeaderAndIndexToPackageLimit()
    {
        using var stream = new MemoryStream();
        var tiles = new[]
        {
            new TilePackageWriter.PackagedTile(0, 0, 0, [0x01]),
            new TilePackageWriter.PackagedTile(0, 128, 0, [0x02]),
        };
        var limits = new CompactTilePackageLimits(
            BundleDataOffset + sizeof(uint) + 1L,
            (BundleDataOffset * 2L) - 1L);

        var act = () => CompactTilePackageWriter.WriteAsync(
            stream,
            "Layers",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None,
            limits);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*package admission limit*");
    }

    private static async Task<byte[]> WritePackageAsync(IEnumerable<TilePackageWriter.PackagedTile> tiles)
    {
        using var stream = new MemoryStream();
        var written = await CompactTilePackageWriter.WriteAsync(
            stream,
            "Sample Cache",
            "PNG",
            [-180d, -85d, 180d, 85d],
            ToAsync(tiles),
            CancellationToken.None);
        written.Should().Be(3);
        return stream.ToArray();
    }

    private static void AssertBundleHeader(byte[] bundle, uint maximumTileSize)
    {
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(0, 4)).Should().Be(3);
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(4, 4)).Should().Be(16_384);
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(8, 4)).Should().Be(maximumTileSize);
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(12, 4)).Should().Be(5);
        BinaryPrimitives.ReadUInt64LittleEndian(bundle.AsSpan(24, 8)).Should().Be((ulong)bundle.Length);
        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(60, 4)).Should().Be(131_072);
    }

    private static byte[] ReadTile(byte[] bundle, int relativeRow, int relativeColumn)
    {
        var indexOffset = 64 + (8 * ((128 * relativeRow) + relativeColumn));
        var index = BinaryPrimitives.ReadUInt64LittleEndian(bundle.AsSpan(indexOffset, 8));
        var tileOffset = index & 0xFF_FF_FF_FF_FFUL;
        var tileSize = index >> 40;
        if (tileSize == 0)
        {
            return [];
        }

        BinaryPrimitives.ReadUInt32LittleEndian(bundle.AsSpan(checked((int)tileOffset - 4), 4))
            .Should().Be((uint)tileSize);
        return bundle.AsSpan(checked((int)tileOffset), checked((int)tileSize)).ToArray();
    }

    private static async Task<JsonDocument> ReadJsonAsync(ZipArchive archive, string path)
        => JsonDocument.Parse(await ReadEntryAsync(archive, path));

    private static async Task<byte[]> ReadEntryAsync(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);
        entry.Should().NotBeNull();
        await using var entryStream = entry!.Open();
        using var buffer = new MemoryStream();
        await entryStream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async IAsyncEnumerable<TilePackageWriter.PackagedTile> ToAsync(
        IEnumerable<TilePackageWriter.PackagedTile> tiles)
    {
        foreach (var tile in tiles)
        {
            yield return tile;
        }

        await Task.CompletedTask;
    }
}
