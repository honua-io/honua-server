// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.TileCachePackage.Domain;
using Honua.Core.Features.TileCachePackage.Services;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Features.TileCachePackage;

/// <summary>
/// Unit tests for <see cref="EsriTileCachePackageReader"/>, the read-only parser for
/// Esri tile/vector-tile cache packages (#1269). Fixtures are synthesized in-memory
/// from the documented Compact Cache V2 / exploded-raster layouts.
/// </summary>
public sealed class EsriTileCachePackageReaderTests
{
    private const int CompactV2HeaderSize = 64;
    private const int CompactV2IndexEntries = 128 * 128;
    private const int CompactV2IndexSize = CompactV2IndexEntries * 8;

    [Theory]
    [InlineData("basemap.tpk", true)]
    [InlineData("basemap.tpkx", true)]
    [InlineData("basemap.vtpk", true)]
    [InlineData("basemap.TPKX", true)]
    [InlineData("basemap.gpkg", false)]
    [InlineData("basemap.zip", false)]
    public void CanRead_MatchesTilePackageExtensions(string fileName, bool expected)
    {
        var reader = new EsriTileCachePackageReader();
        reader.CanRead(fileName).Should().Be(expected);
    }

    [Fact]
    public async Task ReadDescriptor_CompactV2Tpkx_ParsesRasterScheme()
    {
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 3857, name: "Imagery");
        var reader = new EsriTileCachePackageReader();

        var descriptor = await reader.ReadDescriptorAsync(package);

        descriptor.StorageFormat.Should().Be(TileCacheStorageFormat.CompactV2);
        descriptor.DataType.Should().Be(TileCacheDataType.Raster);
        descriptor.ContentType.Should().Be("image/png");
        descriptor.TileMatrixSetIdentifier.Should().Be("WebMercatorQuad");
        descriptor.Title.Should().Be("Imagery");
        descriptor.TileBundlesPath.Should().Be("tile");
    }

    [Fact]
    public async Task ReadDescriptor_CompactV2Vtpk_ParsesVectorSchemeUnderP12Prefix()
    {
        using var package = BuildCompactV2Package(prefix: "p12", format: "pbf", wkid: 4326, name: "Streets");
        var reader = new EsriTileCachePackageReader();

        var descriptor = await reader.ReadDescriptorAsync(package);

        descriptor.DataType.Should().Be(TileCacheDataType.Vector);
        descriptor.ContentType.Should().Be("application/vnd.mapbox-vector-tile");
        descriptor.TileMatrixSetIdentifier.Should().Be("WorldCRS84Quad");
        descriptor.TileBundlesPath.Should().Be("p12/tile");
    }

    [Fact]
    public async Task ReadTiles_CompactV2_DecodesIndexedTilesAtCorrectCoordinates()
    {
        // Place two tiles in the level-3 bundle whose block base is (row 0, col 0):
        //   global (z=3, row=2, col=5) and (z=3, row=7, col=1).
        var tiles = new (int Row, int Col, byte[] Bytes)[]
        {
            (2, 5, Encoding.ASCII.GetBytes("TILE-A")),
            (7, 1, Encoding.ASCII.GetBytes("TILE-BBBB"))
        };
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 3857, name: "x", level: 3, tiles: tiles);
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);

        var read = new List<TileCachePackageTile>();
        await foreach (var tile in reader.ReadTilesAsync(package, descriptor, 0, 24))
        {
            read.Add(tile);
        }

        read.Should().HaveCount(2);
        read.Should().ContainSingle(t => t.Z == 3 && t.X == 5 && t.Y == 2)
            .Which.Content.Should().Equal(Encoding.ASCII.GetBytes("TILE-A"));
        read.Should().ContainSingle(t => t.Z == 3 && t.X == 1 && t.Y == 7)
            .Which.Content.Should().Equal(Encoding.ASCII.GetBytes("TILE-BBBB"));
    }

    [Fact]
    public async Task ReadTiles_CompactV2_HonorsZoomRange()
    {
        var tiles = new (int Row, int Col, byte[] Bytes)[] { (0, 0, [1, 2, 3]) };
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 3857, name: "x", level: 3, tiles: tiles);
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);

        var read = new List<TileCachePackageTile>();
        await foreach (var tile in reader.ReadTilesAsync(package, descriptor, 4, 24))
        {
            read.Add(tile);
        }

        read.Should().BeEmpty("the only bundle is at level 3 which is outside the requested 4-24 range");
    }

    [Fact]
    public async Task ReadDescriptor_ExplodedTpk_ParsesFromConfXml()
    {
        using var package = BuildExplodedPackage();
        var reader = new EsriTileCachePackageReader();

        var descriptor = await reader.ReadDescriptorAsync(package);

        descriptor.StorageFormat.Should().Be(TileCacheStorageFormat.Exploded);
        descriptor.DataType.Should().Be(TileCacheDataType.Raster);
        descriptor.ContentType.Should().Be("image/jpeg");
        descriptor.MinLevel.Should().Be(0);
        descriptor.MaxLevel.Should().Be(2);
    }

    [Fact]
    public async Task ReadTiles_ExplodedTpk_DecodesHexCoordinates()
    {
        using var package = BuildExplodedPackage();
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);

        var read = new List<TileCachePackageTile>();
        await foreach (var tile in reader.ReadTilesAsync(package, descriptor, 0, 24))
        {
            read.Add(tile);
        }

        // _alllayers/L02/R0000000a/C00000014.jpg -> z=2, row=0x0a=10, col=0x14=20.
        read.Should().ContainSingle(t => t.Z == 2 && t.Y == 10 && t.X == 20);
    }

    /// <summary>
    /// Regression test for BH-020: tiles whose encoded byte-size has bit 23 set
    /// (≥ 8,388,608 bytes = 8 MB) must not be silently dropped.  The previous code
    /// used an arithmetic right-shift on the signed Int64 index value, which sign-
    /// extended negative Int64s into negative Int32 sizes, triggering the size-&lt;=0
    /// guard and skipping the tile.
    /// </summary>
    [Fact]
    public async Task ReadTiles_CompactV2_TilesLargerThan8MB_AreNotSilentlyDropped()
    {
        // 8,388,608 bytes (8 MiB exactly) sets bit 23 of the 24-bit size field.
        // Combined with a typical offset, bit 63 of the Int64 index entry is set,
        // making it negative — the arithmetic-shift bug produces a negative size.
        var tileContent = new byte[8_388_608];
        tileContent[0] = 0xAB;
        tileContent[^1] = 0xCD;

        var tiles = new (int Row, int Col, byte[] Bytes)[] { (0, 0, tileContent) };
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 3857, name: "large", level: 0, tiles: tiles);
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);

        var read = new List<TileCachePackageTile>();
        await foreach (var tile in reader.ReadTilesAsync(package, descriptor, 0, 24))
        {
            read.Add(tile);
        }

        read.Should().HaveCount(1, "a tile with size ≥ 8 MiB must not be silently skipped");
        read[0].Content.Length.Should().Be(8_388_608);
        read[0].Content[0].Should().Be(0xAB);
        read[0].Content[^1].Should().Be(0xCD);
    }

    [UnitTest]
    public async Task ReadDescriptor_StatePlanePackage_IsRejected()
    {
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 2263, name: "ny");
        var reader = new EsriTileCachePackageReader();
        var act = async () => await reader.ReadDescriptorAsync(package);
        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*2263*");
    }

    [UnitTest]
    public async Task ReadDescriptor_MissingWkidMatchingMercator_RecordsAssumption()
    {
        using var package = BuildCompactV2Package(prefix: string.Empty, format: "PNG", wkid: 0, name: "unknown");
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);
        descriptor.TileMatrixSetIdentifier.Should().Be("WebMercatorQuad");
        descriptor.Assumption.Should().Be(TileCachePackageScheme.MissingWkidWebMercatorAssumption);
    }

    [UnitTest]
    public async Task ReadTiles_GzipVectorTile_PreservesMagicAndEncoding()
    {
        byte[] gzip;
        using (var ms = new MemoryStream())
        {
            using (var gzipStream = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            {
                gzipStream.Write(Encoding.ASCII.GetBytes("mvt"));
            }

            gzip = ms.ToArray();
        }

        gzip[0].Should().Be(0x1F);
        gzip[1].Should().Be(0x8B);
        var tiles = new (int Row, int Col, byte[] Bytes)[] { (1, 2, gzip) };
        using var package = BuildCompactV2Package(prefix: "p12", format: "pbf", wkid: 4326, name: "streets", level: 0, tiles: tiles);
        var reader = new EsriTileCachePackageReader();
        var descriptor = await reader.ReadDescriptorAsync(package);
        var read = new List<TileCachePackageTile>();
        await foreach (var tile in reader.ReadTilesAsync(package, descriptor, 0, 5))
        {
            read.Add(tile);
        }

        read.Should().ContainSingle();
        TileCachePackageTileEncoding.IsGzipCompressed(read[0].Content).Should().BeTrue();
        TileCachePackageTileEncoding.ContentEncoding(read[0].Content).Should().Be("gzip");
    }

    [UnitTest]
    public void ReadBounded_DeclaredLengthOverEntryCap_Throws()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);
        long remaining = EsriTileCachePackageReader.MaxDecompressedBytes;
        var act = () => EsriTileCachePackageReader.ReadBounded(
            stream,
            EsriTileCachePackageReader.MaxDescriptorBytes + 1,
            EsriTileCachePackageReader.MaxDescriptorBytes,
            ref remaining);
        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void ReadBounded_ZeroDeclaredLengthThatExpands_StopsAtCap()
    {
        using var stream = new MemoryStream(new byte[32]);
        long remaining = 8;
        var act = () => EsriTileCachePackageReader.ReadBounded(stream, 0, 8, ref remaining);
        act.Should().Throw<InvalidDataException>();
    }

    [UnitTest]
    public void Scheme_CustomMercatorOrigin_IsRejected()
    {
        var lods = new[]
        {
            new TileCacheLodSample(0, (2d * TileCachePackageScheme.WebMercatorHalfWorldMeters) / 256d, null)
        };
        var act = () => TileCachePackageScheme.Resolve(3857, -400, 400, 256, lods);
        act.Should().Throw<NotSupportedException>().WithMessage("*3857*WebMercatorQuad*");
    }

    [UnitTest]
    public void Scheme_BritishNationalGrid_IsRejected()
    {
        var act = () => TileCachePackageScheme.Resolve(27700, 0, 0, 256, [new TileCacheLodSample(0, 1, null)]);
        act.Should().Throw<NotSupportedException>().WithMessage("*27700*");
    }

    [Fact]
    public async Task ReadDescriptor_MissingDescriptor_Throws()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var s = entry.Open();
            s.Write(Encoding.ASCII.GetBytes("not a tile package"));
        }

        buffer.Position = 0;
        var reader = new EsriTileCachePackageReader();
        var act = async () => await reader.ReadDescriptorAsync(buffer);
        await act.Should().ThrowAsync<InvalidDataException>();
    }

    // -------- fixture builders --------

    private static MemoryStream BuildCompactV2Package(
        string prefix,
        string format,
        int wkid,
        string name,
        int level = 0,
        (int Row, int Col, byte[] Bytes)[]? tiles = null)
    {
        var buffer = new MemoryStream();
        var root = prefix.Length == 0 ? string.Empty : prefix + "/";

        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, $"{root}root.json", Encoding.UTF8.GetBytes($$"""
                {
                  "name": "{{name}}",
                  "tileBundlesPath": "./tile",
                  "storageInfo": { "storageFormat": "esriMapCacheStorageModeCompactV2", "packetSize": 128 },
                  "tileInfo": {
                    "rows": 256, "cols": 256, "format": "{{format}}",
                    "spatialReference": { "wkid": {{wkid.ToString(CultureInfo.InvariantCulture)}}, "latestWkid": {{wkid.ToString(CultureInfo.InvariantCulture)}} },
                    {{SchemeJson(wkid, level)}}
                  }
                }
                """));

            if (tiles is { Length: > 0 })
            {
                var bundle = BuildCompactV2Bundle(tiles);
                // Bundle base is the top-left of the 128x128 block; all test tiles
                // sit in block (0,0), so the bundle file is R0000C0000.bundle.
                WriteEntry(archive, $"{root}tile/L{level:D2}/R0000C0000.bundle", bundle);
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private static byte[] BuildCompactV2Bundle((int Row, int Col, byte[] Bytes)[] tiles)
    {
        // Header (64) + index (16384 * 8) + tile data appended sequentially.
        var dataStart = CompactV2HeaderSize + CompactV2IndexSize;
        using var ms = new MemoryStream();
        var header = new byte[CompactV2HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 3);       // version
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), CompactV2IndexEntries);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), 5);      // offset byte count
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), 40);     // user header offset
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60, 4), CompactV2IndexSize);
        ms.Write(header);

        var index = new byte[CompactV2IndexSize];
        using var tileBlob = new MemoryStream();
        long cursor = dataStart;
        foreach (var (row, col, bytes) in tiles)
        {
            var entry = row * 128 + col; // row-major within the block
            long packed = ((long)bytes.Length << 40) | cursor;
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entry * 8, 8), packed);
            tileBlob.Write(bytes);
            cursor += bytes.Length;
        }

        ms.Write(index);
        ms.Write(tileBlob.ToArray());
        return ms.ToArray();
    }

    private static MemoryStream BuildExplodedPackage()
    {
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "conf.xml", Encoding.UTF8.GetBytes($$"""
                <?xml version="1.0"?>
                <CacheInfo>
                  <TileCacheInfo>
                    <SpatialReference><WKID>3857</WKID></SpatialReference>
                    <TileCols>256</TileCols>
                    <TileRows>256</TileRows>
                    <TileOrigin>
                      <X>-20037508.342789244</X>
                      <Y>20037508.342789244</Y>
                    </TileOrigin>
                    <LODInfos>
                      <LODInfo><LevelID>0</LevelID><Resolution>{{Resolution(3857, 0)}}</Resolution></LODInfo>
                      <LODInfo><LevelID>1</LevelID><Resolution>{{Resolution(3857, 1)}}</Resolution></LODInfo>
                      <LODInfo><LevelID>2</LevelID><Resolution>{{Resolution(3857, 2)}}</Resolution></LODInfo>
                    </LODInfos>
                  </TileCacheInfo>
                  <TileImageInfo><CacheTileFormat>JPEG</CacheTileFormat></TileImageInfo>
                  <CacheStorageInfo><StorageFormat>esriMapCacheStorageModeExploded</StorageFormat></CacheStorageInfo>
                </CacheInfo>
                """));

            WriteEntry(archive, "_alllayers/L02/R0000000a/C00000014.jpg", [0xFF, 0xD8, 0xFF, 0xE0]);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static string SchemeJson(int wkid, int level)
    {
        if (wkid == 4326)
        {
            return
                "\"origin\": { \"x\": -180, \"y\": 90 }, " +
                "\"lods\": [ { \"level\": " + level.ToString(CultureInfo.InvariantCulture) +
                ", \"resolution\": " + Resolution(wkid, level) + " } ]";
        }

        return
            "\"origin\": { \"x\": -20037508.342789244, \"y\": 20037508.342789244 }, " +
            "\"lods\": [ { \"level\": " + level.ToString(CultureInfo.InvariantCulture) +
            ", \"resolution\": " + Resolution(wkid, level) + " } ]";
    }

    private static string Resolution(int wkid, int level)
    {
        var resolution0 = wkid == 4326
            ? 180d / 256d
            : (2d * 20037508.342789244) / 256d;
        return (resolution0 / Math.Pow(2d, level)).ToString("R", CultureInfo.InvariantCulture);
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var s = entry.Open();
        s.Write(content);
    }
}
