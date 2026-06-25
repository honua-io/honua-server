// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.TileCachePackage.Domain;
using Honua.Core.Features.TileCachePackage.Services;

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
                    "lods": [ { "level": {{level.ToString(CultureInfo.InvariantCulture)}} } ]
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
        var tileBlob = new MemoryStream();
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
            WriteEntry(archive, "conf.xml", Encoding.UTF8.GetBytes("""
                <?xml version="1.0"?>
                <CacheInfo>
                  <TileCacheInfo>
                    <SpatialReference><WKID>3857</WKID></SpatialReference>
                    <TileCols>256</TileCols>
                    <TileRows>256</TileRows>
                    <LODInfos>
                      <LODInfo><LevelID>0</LevelID></LODInfo>
                      <LODInfo><LevelID>1</LevelID></LODInfo>
                      <LODInfo><LevelID>2</LevelID></LODInfo>
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

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var s = entry.Open();
        s.Write(content);
    }
}
