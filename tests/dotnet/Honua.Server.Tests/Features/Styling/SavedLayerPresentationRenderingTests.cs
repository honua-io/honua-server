// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Styling;

// A layer presentation saved through the admin drawing-info endpoint (the Console "Layer
// presentation" override) is advertised as the layer's drawingInfo, so every server-rendered
// image and legend must draw it too.
[Collection("Database")]
[Protocol(TestProtocols.MapServer)]
public sealed class SavedLayerPresentationRenderingTests : IAsyncLifetime
{
    // Contains the seeded point features (see OgcClassicWmsZoomGatingTests).
    private const string Bbox4326 = "-123,37,-122,38";
    private const string Bbox3857 = "-13692297.3676,4439106.7873,-13580977.8768,4579425.8129";

    private const string RedPointDrawingInfo = """
        {
          "renderer": {
            "type": "simple",
            "symbol": {
              "type": "esriSMS",
              "style": "esriSMSCircle",
              "color": [230, 0, 0, 255],
              "size": 12,
              "outline": { "color": [230, 0, 0, 255], "width": 1 }
            }
          }
        }
        """;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        // WMS applies a default TIME to temporal layers; draw every seeded feature.
        _fixture.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId, clearTemporal: true);
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/export")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/legend")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMS")]
    public async Task SavedDrawingInfo_IsDrawnByExportGetMapAndLegend_AndClearingRestoresDefault()
    {
        HasRedPixel(await ExportAsync()).Should().BeFalse("the default symbol is not red");
        HasRedPixel(await GetMapAsync()).Should().BeFalse("the default symbol is not red");

        await PutDrawingInfoAsync(RedPointDrawingInfo);

        HasRedPixel(await ExportAsync()).Should().BeTrue("MapServer export must draw the saved renderer");
        HasRedPixel(await GetMapAsync()).Should().BeTrue("WMS GetMap must draw the saved renderer");
        (await LegendSwatchesAsync()).Should().Contain(
            swatch => HasRedPixel(swatch), "the legend must show the renderer the map draws");
        HasRedPixel(await GetPngAsync(WmsLegendGraphicUrl)).Should().BeTrue("WMS GetLegendGraphic must match GetMap");

        await PutDrawingInfoAsync("null");

        HasRedPixel(await ExportAsync()).Should().BeFalse("clearing the override restores the catalog style");
        HasRedPixel(await GetMapAsync()).Should().BeFalse("clearing the override restores the catalog style");
    }

    [IntegrationTest]
    [Operation(Operations.Tile)]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/drawing-info")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/tile/{z}/{y}/{x}")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/WMTS")]
    [Endpoint("GET /rest/services/{serviceId}/MapServer/exportTiles")]
    public async Task SavedDrawingInfo_IsDrawnByMapServerTilesAndWmts()
    {
        // Tile paths draw the same saved renderer as export and GetMap.
        HasRedPixel(await GetPngAsync(TileUrl)).Should().BeFalse("the default symbol is not red");
        HasRedPixel(await GetPngAsync(WmtsTileUrl)).Should().BeFalse("the default symbol is not red");

        await PutDrawingInfoAsync(RedPointDrawingInfo);

        HasRedPixel(await GetPngAsync(TileUrl)).Should().BeTrue("MapServer tiles must draw the saved renderer");
        HasRedPixel(await GetPngAsync(WmtsTileUrl)).Should().BeTrue("WMTS GetTile must draw the saved renderer");
        (await ExportTilePackagePngsAsync()).Should().Contain(
            tile => HasRedPixel(tile), "exportTiles must draw the saved renderer");

        await PutDrawingInfoAsync("null");

        HasRedPixel(await GetPngAsync(TileUrl)).Should().BeFalse("clearing the override restores the catalog style");
        HasRedPixel(await GetPngAsync(WmtsTileUrl)).Should().BeFalse("clearing the override restores the catalog style");
    }

    private async Task<List<byte[]>> ExportTilePackagePngsAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/exportTiles" +
            $"?f=json&levels=4&exportExtent={Bbox4326}&exportExtentSR=4326&maxTiles=4&storageFormat=zip");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var result = JsonDocument.Parse(body);
        result.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobSucceeded", body);
        var fileId = result.RootElement.GetProperty("archiveFileId").GetString()!;

        var storage = _fixture.GetService<ICloudFileStorage>();
        try
        {
            var bytes = await storage.DownloadBytesAsync(fileId);
            bytes.Should().NotBeNull();
            using var archive = new ZipArchive(new MemoryStream(bytes!), ZipArchiveMode.Read);
            var tiles = new List<byte[]>();
            foreach (var entry in archive.Entries.Where(item => item.FullName.EndsWith(".png", StringComparison.Ordinal)))
            {
                await using var stream = entry.Open();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                tiles.Add(buffer.ToArray());
            }

            tiles.Should().NotBeEmpty("the package must contain rendered tiles");
            return tiles;
        }
        finally
        {
            await storage.DeleteAsync(fileId);
        }
    }

    private static readonly string WmsLegendGraphicUrl =
        $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS?SERVICE=WMS&REQUEST=GetLegendGraphic&VERSION=1.3.0" +
        $"&LAYER={WebAppFixture.TestLayerId}&FORMAT=image/png";

    // Web Mercator z4 tile (x=2, y=6) contains the seeded points near -122.5, 37.5.
    private const string TileUrl = $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/tile/4/6/2";

    private static readonly string WmtsTileUrl =
        $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMTS?SERVICE=WMTS&REQUEST=GetTile&VERSION=1.0.0" +
        $"&LAYER={WebAppFixture.TestLayerId}&STYLE=default&FORMAT=image/png&TILEMATRIXSET=WebMercatorQuad" +
        "&TILEMATRIX=4&TILEROW=6&TILECOL=2";

    private async Task<byte[]> GetPngAsync(string url)
    {
        var response = await _fixture.Client.GetAsync(url);
        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(content));
        return content;
    }

    private async Task PutDrawingInfoAsync(string json)
    {
        var client = _fixture.CreateAdminClient();
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PutAsync(
            $"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/drawing-info", content);
        response.Be200Ok();
    }

    private async Task<byte[]> ExportAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/export" +
            $"?bbox={Bbox4326}&bboxSR=4326&imageSR=4326&size=256,256&format=png32&transparent=true&f=image");
        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(content));
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        return content;
    }

    private async Task<byte[]> GetMapAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/WMS" +
            $"?SERVICE=WMS&REQUEST=GetMap&VERSION=1.3.0&BBOX={Bbox3857}" +
            "&WIDTH=256&HEIGHT=256&CRS=EPSG:3857" +
            $"&LAYERS={WebAppFixture.TestLayerId}&STYLES=&FORMAT=image/png&TRANSPARENT=true");
        var content = await response.Content.ReadAsByteArrayAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, Encoding.UTF8.GetString(content));
        return content;
    }

    private async Task<List<byte[]>> LegendSwatchesAsync()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{WebAppFixture.TestServiceId}/MapServer/legend?f=json");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var swatches = new List<byte[]>();
        foreach (var layer in document.RootElement.GetProperty("layers").EnumerateArray())
        {
            foreach (var entry in layer.GetProperty("legend").EnumerateArray())
            {
                if (entry.TryGetProperty("imageData", out var imageData) && imageData.GetString() is { Length: > 0 } data)
                {
                    swatches.Add(Convert.FromBase64String(data));
                }
            }
        }

        swatches.Should().NotBeEmpty("the legend must carry swatches");
        return swatches;
    }

    private static bool HasRedPixel(byte[] pngBytes)
    {
        using var bitmap = SKBitmap.Decode(pngBytes);
        bitmap.Should().NotBeNull("the response must be a decodable PNG");

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 0 && pixel.Red > 150 && pixel.Green < 80 && pixel.Blue < 80)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
