// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Honua.Core.Features.Security.Domain;
using Microsoft.AspNetCore.Hosting;
using SkiaSharp;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServer parameter validation: format variations, geometry parsing, valid parameters.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerParameterValidationTests : IClassFixture<ImageServerParameterFixture>
{
    private readonly WebAppFixture _fixture;
    private readonly HttpClient _client;
    private const int TestLayerId = 0;

    public ImageServerParameterValidationTests(ImageServerParameterFixture fixture)
    {
        _fixture = fixture.App;
        _client = fixture.Client;
    }

    #region Service Info Response Structure

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_ValidRequest_ReturnsExpectedStructure()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=json");

        await AssertMetadataAsync(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        // Verify required Esri ImageServer properties. Honua does not advertise an ArcGIS
        // Server version (see NoArcGisServerVersionTests).
        root.TryGetProperty("currentVersion", out _).Should().BeFalse();
        root.TryGetProperty("serviceDescription", out _).Should().BeTrue();
        root.TryGetProperty("name", out _).Should().BeTrue();
        root.TryGetProperty("extent", out var extent).Should().BeTrue();
        root.TryGetProperty("spatialReference", out _).Should().BeTrue();
        root.TryGetProperty("bandCount", out _).Should().BeTrue();
        root.TryGetProperty("pixelType", out _).Should().BeTrue();
        root.TryGetProperty("capabilities", out var caps).Should().BeTrue();

        // Verify extent has required sub-properties
        extent.TryGetProperty("xmin", out _).Should().BeTrue();
        extent.TryGetProperty("ymin", out _).Should().BeTrue();
        extent.TryGetProperty("xmax", out _).Should().BeTrue();
        extent.TryGetProperty("ymax", out _).Should().BeTrue();
        extent.TryGetProperty("spatialReference", out _).Should().BeTrue();

        // Verify capabilities include expected values
        caps.GetString().Should().Contain("Image");
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_WithoutFormatParameter_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer");

        await AssertMetadataAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_PjsonFormat_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=pjson");

        await AssertMetadataAsync(response);
    }

    #endregion

    #region Export Image Parameters

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithAllValidParameters_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&size=512,512&format=png" +
            "&imageSr=4326&bboxSr=4326&interpolation=RSP_BilinearInterpolation" +
            "&compressionQuality=85");

        await AssertExportAsync(response, 512, 512);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_JpegFormat_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&format=jpeg");

        await AssertExportAsync(response, 400, 400, "image/jpeg");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_TiffFormat_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&format=tiff");

        await AssertExportAsync(response, 400, 400, "image/tiff");
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_ProjectedBbox_ReturnsSeededRaster()
    {
        // Web Mercator bbox
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,445277.96317309426,222684.20850554405&bboxSr=3857&imageSr=3857");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEpsgPrefixedSpatialReferences_ReturnsSeededRaster()
    {
        var imageSr = Uri.EscapeDataString("EPSG:4326");
        var bboxSr = Uri.EscapeDataString("urn:ogc:def:crs:EPSG::4326");

        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=0,0,4,2&imageSr={imageSr}&bboxSr={bboxSr}");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithSafeCurieSpatialReferences_ReturnsSeededRaster()
    {
        var imageSr = Uri.EscapeDataString("[EPSG:4326]");
        var bboxSr = Uri.EscapeDataString("[OGC:CRS84]");

        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=0,0,4,2&imageSr={imageSr}&bboxSr={bboxSr}");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEsriJsonSpatialReferences_ReturnsSeededRaster()
    {
        // ArcGIS SDK clients send spatial references as Esri JSON, e.g. bboxSR={"wkid":4326}.
        var imageSr = Uri.EscapeDataString("{\"wkid\":4326}");
        var bboxSr = Uri.EscapeDataString("{\"wkid\":4326}");

        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=0,0,4,2&imageSr={imageSr}&bboxSr={bboxSr}");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEsriJsonLatestWkidSpatialReference_ReturnsSeededRaster()
    {
        var bboxSr = Uri.EscapeDataString("{\"latestWkid\":3857}");

        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=0,0,445277.96317309426,222684.20850554405&bboxSr={bboxSr}");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_PostFormBody_WithEsriJsonSpatialReferences_ReturnsSeededRaster()
    {
        // The ArcGIS API for Python ImageryLayer.export_image POSTs a form body
        // carrying the spatial references in Esri JSON form (bboxSR={"wkid":4326}).
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("bbox", "0,0,4,2"),
            new KeyValuePair<string, string>("size", "256,256"),
            new KeyValuePair<string, string>("format", "png"),
            new KeyValuePair<string, string>("bboxSR", "{\"wkid\":4326}"),
            new KeyValuePair<string, string>("imageSR", "{\"wkid\":4326}"),
        });

        var response = await PostSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage",
            content);

        await AssertExportAsync(response, 256, 256);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_PostJsonBody_WithEsriJsonSpatialReferences_ReturnsSeededRaster()
    {
        // ArcGIS clients may also POST a JSON body where bboxSR/imageSR are nested
        // JSON objects ({"wkid":4326}) rather than strings.
        using var content = new StringContent(
            "{\"f\":\"json\",\"bbox\":\"0,0,4,2\",\"size\":\"256,256\"," +
            "\"format\":\"png\",\"bboxSR\":{\"wkid\":4326},\"imageSR\":{\"latestWkid\":3857}}",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await PostSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage",
            content);

        await AssertExportAsync(response, 256, 256);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_MinimumSize_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&size=1,1");

        await AssertExportAsync(response, 1, 1);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_MaximumSize_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&size=4096,4096");

        await AssertExportAsync(response, 4096, 4096);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_NoBbox_UsesDefaultExtent()
    {
        // When bbox is not provided, handler uses raster extent
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=json");

        await AssertExportAsync(response, 400, 400);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_SuccessResponse_HasExpectedStructure()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=0,0,4,2&format=png");

        await AssertExportAsync(response, 400, 400);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.TryGetProperty("href", out var href).Should().BeTrue();
        href.GetString().Should().NotBeNullOrEmpty();
        root.TryGetProperty("width", out _).Should().BeTrue();
        root.TryGetProperty("height", out _).Should().BeTrue();
        root.TryGetProperty("extent", out _).Should().BeTrue();
    }

    #endregion

    #region Identify Parameters

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_CommaSeparatedGeometry_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=3.5,1&f=json");

        await AssertIdentifyAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_JsonGeometry_ReturnsSeededRaster()
    {
        var geometry = Uri.EscapeDataString("{\"x\":3.5,\"y\":1}");
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry={geometry}&f=json");

        await AssertIdentifyAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithSrid_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=3.5,1&sr=4326&f=json");

        await AssertIdentifyAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithOgcCrsUri_ReturnsSeededRaster()
    {
        var sr = Uri.EscapeDataString("http://www.opengis.net/def/crs/OGC/1.3/CRS84");

        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=3.5,1&sr={sr}&f=json");

        await AssertIdentifyAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithReturnCatalogItems_ReturnsSeededRaster()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify" +
            "?geometry=3.5,1&f=json&returnCatalogItems=true");

        await AssertIdentifyAsync(response);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_SuccessResponse_HasExpectedStructure()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=3.5,1&f=json");

        await AssertIdentifyAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;

        root.TryGetProperty("location", out var location).Should().BeTrue();
        location.TryGetProperty("x", out _).Should().BeTrue();
        location.TryGetProperty("y", out _).Should().BeTrue();
        root.TryGetProperty("value", out _).Should().BeTrue();
        root.TryGetProperty("properties", out _).Should().BeTrue();
    }

    #endregion

    #region Tile Parameters

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_PngFormat_ReturnsExpectedContentType()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0?format=png");

        await AssertImageAsync(response, "image/png", 256, 256);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_JpegFormat_ReturnsExpectedContentType()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0?format=jpeg");

        await AssertImageAsync(response, "image/jpeg", 256, 256);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_DefaultFormat_ReturnsPng()
    {
        var response = await GetSeededAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0");

        await AssertImageAsync(response, "image/png", 256, 256);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_VariousZoomLevels_ReturnsExpectedStatus()
    {
        // Test multiple zoom levels
        foreach (var level in new[] { 0, 5, 10, 18 })
        {
            var response = await GetSeededAsync(
                $"/rest/services/{TestLayerId}/ImageServer/tile/{level}/0/0");

            if (level == 0)
            {
                await AssertImageAsync(response, "image/png", 256, 256);
            }
            else
            {
                // At higher zooms tile (0,0) is far from the seeded 0..4E, 0..2N raster.
                await response.AssertGeoServicesErrorAsync(404);
            }
        }
    }

    #endregion

    private async Task<HttpResponseMessage> GetSeededAsync(string url)
    {
        HttpResponseMessage response = null!;
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_fixture, async () =>
        {
            using var denied = await _fixture.Client.GetAsync(url);
            await AssertDeniedAsync(denied);
            response = await _client.GetAsync(url);
        });
        return response;
    }

    private async Task<HttpResponseMessage> PostSeededAsync(string url, HttpContent content)
    {
        HttpResponseMessage response = null!;
        await RasterIntegrationTestData.RunWithIssue522MosaicAsync(_fixture, async () =>
        {
            using var denied = await _fixture.Client.PostAsync(url, content);
            await AssertDeniedAsync(denied);
            response = await _client.PostAsync(url, content);
        });
        return response;
    }

    private static async Task AssertDeniedAsync(HttpResponseMessage response)
    {
        await response.AssertGeoServicesErrorAsync(499);
        response.Content.Headers.ContentType?.MediaType.Should().NotStartWith("image/");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var field in new[] { "href", "properties", "value", "extent", "name", "bandCount" })
        {
            document.RootElement.TryGetProperty(field, out _).Should().BeFalse(
                "an anonymous caller must receive no raster data or metadata");
        }
    }

    private static async Task AssertMetadataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(body);
        document.RootElement.GetProperty("bandCount").GetInt32().Should().Be(1);
        var extent = document.RootElement.GetProperty("extent");
        extent.GetProperty("xmin").GetDouble().Should().Be(0);
        extent.GetProperty("ymin").GetDouble().Should().Be(0);
        extent.GetProperty("xmax").GetDouble().Should().Be(4);
        extent.GetProperty("ymax").GetDouble().Should().Be(2);
    }

    private async Task AssertExportAsync(HttpResponseMessage response, int width, int height, string mediaType = "image/png")
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse(body);
        root.GetProperty("width").GetInt32().Should().Be(width);
        root.GetProperty("height").GetInt32().Should().Be(height);
        root.GetProperty("extent").GetProperty("xmax").GetDouble().Should()
            .BeGreaterThan(root.GetProperty("extent").GetProperty("xmin").GetDouble());
        var href = root.GetProperty("href").GetString();
        href.Should().NotBeNullOrWhiteSpace();
        using var image = await _client.GetAsync(href);
        image.StatusCode.Should().Be(HttpStatusCode.OK);
        (await image.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
        image.Content.Headers.ContentType?.MediaType.Should().Be(mediaType);
        if (mediaType is "image/png" or "image/jpeg")
        {
            await AssertImageAsync(image, mediaType, width, height);
        }
    }

    private static async Task AssertIdentifyAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("properties").GetProperty("Band_1").GetDouble().Should().Be(40);
        document.RootElement.GetProperty("value").GetString().Should().Be("40");
        var location = document.RootElement.GetProperty("location");
        location.GetProperty("x").GetDouble().Should().Be(3.5);
        location.GetProperty("y").GetDouble().Should().Be(1);
    }

    private static async Task AssertImageAsync(HttpResponseMessage response, string mediaType, int width, int height)
    {
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be(mediaType);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        bitmap.Should().NotBeNull("the response must be a decodable image, not a signature or error envelope");
        bitmap.Width.Should().Be(width);
        bitmap.Height.Should().Be(height);
    }
}

/// <summary>Authenticated ImageServer host with a restricted raster resource.</summary>
public sealed class ImageServerParameterFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        });

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await App.InitializeAsync();
        App.UpdateV2ResourceMetadata(WebAppFixture.TestLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["imagery-reader"] });
        Client = App.CreateClient(client =>
            client.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword));
    }

    public Task DisposeAsync() => App.DisposeAsync();
}
