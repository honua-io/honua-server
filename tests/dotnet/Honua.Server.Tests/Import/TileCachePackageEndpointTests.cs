// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for the Esri tile-cache package import + serving binding (#1269).
/// Imports a synthetic Compact Cache V2 (.tpkx) package and then serves the imported
/// tile + TileJSON descriptor through the public tile endpoints.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class TileCachePackageEndpointTests : IAsyncLifetime
{
    private const int CompactV2HeaderSize = 64;
    private const int CompactV2IndexEntries = 128 * 128;
    private const int CompactV2IndexSize = CompactV2IndexEntries * 8;

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/tile-package")]
    public async Task ImportTilePackage_WithMissingTilesetId_ReturnsBadRequest()
    {
        using var content = new ByteArrayContent([1, 2, 3]);
        var response = await _client.PostAsync("/api/v1/admin/import/tile-package?fileName=x.tpkx", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/tile-package")]
    public async Task ImportTilePackage_WithUnknownFormat_ReturnsBadRequest()
    {
        using var content = new ByteArrayContent([1, 2, 3]);
        var response = await _client.PostAsync(
            "/api/v1/admin/import/tile-package?tilesetId=demo&fileName=x.gpkg", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/tile-package")]
    public async Task ImportTilePackage_DryRun_ReportsPlanWithoutWriting()
    {
        var package = BuildCompactV2Package(
            tiles: [(2, 5, Encoding.ASCII.GetBytes("PNGDATA"))], level: 3);
        using var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var response = await _client.PostAsync(
            "/api/v1/admin/import/tile-package?tilesetId=dryrun-demo&fileName=demo.tpkx&dryRun=true", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("dataType").GetString().Should().Be("raster");
        doc.RootElement.GetProperty("tilesImported").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/imported/{tileCacheId}/{z}/{x}/{y}")]
    public async Task ImportTilePackage_ThenServesTileAndTileSet()
    {
        var tileBytes = Encoding.ASCII.GetBytes("RASTER-TILE-BYTES");
        // global (z=3, row=2, col=5)
        var package = BuildCompactV2Package(tiles: [(2, 5, tileBytes)], level: 3);

        using var content = new ByteArrayContent(package);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        var importResponse = await _client.PostAsync(
            "/api/v1/admin/import/tile-package?tilesetId=served-demo&fileName=served.tpkx", content);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using var importDoc = JsonDocument.Parse(await importResponse.Content.ReadAsStringAsync());
        var root = importDoc.RootElement;
        root.GetProperty("success").GetBoolean().Should().BeTrue();
        root.GetProperty("tilesImported").GetInt32().Should().Be(1);
        var tileCacheId = root.GetProperty("tileCacheId").GetString();
        tileCacheId.Should().NotBeNullOrEmpty();

        // Serving binding: TileJSON descriptor.
        var tileSetResponse = await _client.GetAsync($"/tiles/imported/{tileCacheId}");
        tileSetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var tileSetDoc = JsonDocument.Parse(await tileSetResponse.Content.ReadAsStringAsync());
        tileSetDoc.RootElement.GetProperty("dataType").GetString().Should().Be("raster");
        tileSetDoc.RootElement.GetProperty("format").GetString().Should().Be("image/png");

        // Serving binding: actual tile bytes round-trip.
        var tileResponse = await _client.GetAsync($"/tiles/imported/{tileCacheId}/3/5/2");
        tileResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var served = await tileResponse.Content.ReadAsByteArrayAsync();
        served.Should().Equal(tileBytes);

        // Missing tile -> 404.
        var missing = await _client.GetAsync($"/tiles/imported/{tileCacheId}/3/99/99");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("GET /tiles/imported/{tileCacheId}")]
    public async Task GetImportedTileSet_UnknownCache_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/tiles/imported/tilecache:nope:nope:deadbeef");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static byte[] BuildCompactV2Package((int Row, int Col, byte[] Bytes)[] tiles, int level)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "root.json", Encoding.UTF8.GetBytes($$"""
                {
                  "name": "Demo",
                  "tileBundlesPath": "./tile",
                  "storageInfo": { "storageFormat": "esriMapCacheStorageModeCompactV2", "packetSize": 128 },
                  "tileInfo": {
                    "rows": 256, "cols": 256, "format": "PNG",
                    "spatialReference": { "wkid": 3857, "latestWkid": 3857 },
                    "lods": [ { "level": {{level.ToString(CultureInfo.InvariantCulture)}} } ]
                  }
                }
                """));

            WriteEntry(archive, $"tile/L{level:D2}/R0000C0000.bundle", BuildBundle(tiles));
        }

        return buffer.ToArray();
    }

    private static byte[] BuildBundle((int Row, int Col, byte[] Bytes)[] tiles)
    {
        var dataStart = CompactV2HeaderSize + CompactV2IndexSize;
        using var ms = new MemoryStream();
        var header = new byte[CompactV2HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(0, 4), 3);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), CompactV2IndexEntries);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12, 4), 5);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(32, 8), 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60, 4), CompactV2IndexSize);
        ms.Write(header);

        var index = new byte[CompactV2IndexSize];
        using var blob = new MemoryStream();
        long cursor = dataStart;
        foreach (var (row, col, bytes) in tiles)
        {
            var entry = (row * 128) + col;
            long packed = ((long)bytes.Length << 40) | cursor;
            BinaryPrimitives.WriteInt64LittleEndian(index.AsSpan(entry * 8, 8), packed);
            blob.Write(bytes);
            cursor += bytes.Length;
        }

        ms.Write(index);
        ms.Write(blob.ToArray());
        return ms.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name);
        using var s = entry.Open();
        s.Write(content);
    }
}
