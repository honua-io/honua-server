// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

/// <summary>
/// Tests for ImageServer parameter validation: format variations, geometry parsing, valid parameters.
/// </summary>
[Collection("Database.GeoServicesRaster")]
[Protocol(TestProtocols.ImageServer)]
public class ImageServerParameterValidationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Service Info Response Structure

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_ValidRequest_ReturnsExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            // Verify required Esri ImageServer properties
            root.TryGetProperty("currentVersion", out _).Should().BeTrue();
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
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_WithoutFormatParameter_DoesNotReturnBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{id}/ImageServer")]
    public async Task GetServiceInfo_PjsonFormat_DoesNotReturnBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer?f=pjson");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    #endregion

    #region Export Image Parameters

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithAllValidParameters_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&size=512,512&format=png" +
            "&imageSr=4326&bboxSr=4326&interpolation=RSP_BilinearInterpolation" +
            "&compressionQuality=85");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_JpegFormat_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&format=jpeg");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_TiffFormat_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&format=tiff");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_ProjectedBbox_ReturnsSuccessOrNotFound()
    {
        // Web Mercator bbox
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-20037508,-20037508,20037508,20037508&imageSr=3857");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEpsgPrefixedSpatialReferences_ReturnsSuccessOrNotFound()
    {
        var imageSr = Uri.EscapeDataString("EPSG:4326");
        var bboxSr = Uri.EscapeDataString("urn:ogc:def:crs:EPSG::4326");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=-180,-90,180,90&imageSr={imageSr}&bboxSr={bboxSr}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithSafeCurieSpatialReferences_ReturnsSuccessOrNotFound()
    {
        var imageSr = Uri.EscapeDataString("[EPSG:4326]");
        var bboxSr = Uri.EscapeDataString("[OGC:CRS84]");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=-180,-90,180,90&imageSr={imageSr}&bboxSr={bboxSr}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEsriJsonSpatialReferences_ReturnsSuccessOrNotFound()
    {
        // ArcGIS SDK clients send spatial references as Esri JSON, e.g. bboxSR={"wkid":4326}.
        var imageSr = Uri.EscapeDataString("{\"wkid\":4326}");
        var bboxSr = Uri.EscapeDataString("{\"wkid\":4326}");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=-180,-90,180,90&imageSr={imageSr}&bboxSr={bboxSr}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_WithEsriJsonLatestWkidSpatialReference_ReturnsSuccessOrNotFound()
    {
        var bboxSr = Uri.EscapeDataString("{\"latestWkid\":3857}");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            $"?f=json&bbox=-180,-90,180,90&bboxSr={bboxSr}");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_PostFormBody_WithEsriJsonSpatialReferences_DoesNotReturnBadRequest()
    {
        // The ArcGIS API for Python ImageryLayer.export_image POSTs a form body
        // carrying the spatial references in Esri JSON form (bboxSR={"wkid":4326}).
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("bbox", "-180,-90,180,90"),
            new KeyValuePair<string, string>("size", "256,256"),
            new KeyValuePair<string, string>("format", "png"),
            new KeyValuePair<string, string>("bboxSR", "{\"wkid\":4326}"),
            new KeyValuePair<string, string>("imageSR", "{\"wkid\":4326}"),
        });

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage",
            content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("POST /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_PostJsonBody_WithEsriJsonSpatialReferences_DoesNotReturnBadRequest()
    {
        // ArcGIS clients may also POST a JSON body where bboxSR/imageSR are nested
        // JSON objects ({"wkid":4326}) rather than strings.
        using var content = new StringContent(
            "{\"f\":\"json\",\"bbox\":\"-180,-90,180,90\",\"size\":\"256,256\"," +
            "\"format\":\"png\",\"bboxSR\":{\"wkid\":4326},\"imageSR\":{\"latestWkid\":3857}}",
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await _fixture.Client.PostAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage",
            content);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_MinimumSize_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&size=1,1");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_MaximumSize_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&size=4096,4096");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_NoBbox_UsesDefaultExtent()
    {
        // When bbox is not provided, handler uses raster extent
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage?f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Export)]
    [Endpoint("GET /rest/services/{id}/ImageServer/exportImage")]
    public async Task ExportImage_SuccessResponse_HasExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/exportImage" +
            "?f=json&bbox=-180,-90,180,90&format=png");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            root.TryGetProperty("href", out var href).Should().BeTrue();
            href.GetString().Should().NotBeNullOrEmpty();
            root.TryGetProperty("width", out _).Should().BeTrue();
            root.TryGetProperty("height", out _).Should().BeTrue();
            root.TryGetProperty("extent", out _).Should().BeTrue();
        }
    }

    #endregion

    #region Identify Parameters

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_CommaSeparatedGeometry_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=0,0&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_JsonGeometry_ReturnsSuccessOrNotFound()
    {
        var geometry = Uri.EscapeDataString("{\"x\":-122.4194,\"y\":37.7749}");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry={geometry}&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithSrid_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=0,0&sr=4326&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithOgcCrsUri_ReturnsSuccessOrNotFound()
    {
        var sr = Uri.EscapeDataString("http://www.opengis.net/def/crs/OGC/1.3/CRS84");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=0,0&sr={sr}&f=json");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_WithReturnCatalogItems_ReturnsSuccessOrNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify" +
            "?geometry=0,0&f=json&returnCatalogItems=true");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Identify)]
    [Endpoint("GET /rest/services/{id}/ImageServer/identify")]
    public async Task Identify_SuccessResponse_HasExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/identify?geometry=0,0&f=json");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var root = json.RootElement;

            root.TryGetProperty("location", out var location).Should().BeTrue();
            location.TryGetProperty("x", out _).Should().BeTrue();
            location.TryGetProperty("y", out _).Should().BeTrue();
            root.TryGetProperty("value", out _).Should().BeTrue();
            root.TryGetProperty("properties", out _).Should().BeTrue();
        }
    }

    #endregion

    #region Tile Parameters

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_PngFormat_ReturnsExpectedContentType()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0?format=png");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_JpegFormat_ReturnsExpectedContentType()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0?format=jpeg");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_DefaultFormat_ReturnsPng()
    {
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/{TestLayerId}/ImageServer/tile/0/0/0");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /rest/services/{id}/ImageServer/tile/{level}/{row}/{col}")]
    public async Task GetImageTile_VariousZoomLevels_ReturnsExpectedStatus()
    {
        // Test multiple zoom levels
        foreach (var level in new[] { 0, 5, 10, 18 })
        {
            var response = await _fixture.Client.GetAsync(
                $"/rest/services/{TestLayerId}/ImageServer/tile/{level}/0/0");

            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK, HttpStatusCode.NoContent, HttpStatusCode.NotFound);
        }
    }

    #endregion
}
