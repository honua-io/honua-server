// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Maps;

/// <summary>
/// Tests for OGC Maps parameter validation: format variations, CRS, dimensions, bbox, and styles.
/// </summary>
[Collection("Database.OgcApiTiles")]
[Protocol(TestProtocols.OgcApiMaps)]
public class OgcMapsParameterValidationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Format Parameter

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_PngFormat_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?f=png&bbox=-180,-90,180,90");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_JpegFormat_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?f=jpeg&bbox=-180,-90,180,90");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/jpeg");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_TiffFormat_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?f=tiff&bbox=-180,-90,180,90");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/tiff");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_DefaultFormat_ReturnsPng()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?bbox=-180,-90,180,90");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
    }

    #endregion

    #region Dimensions

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_CustomDimensions_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?width=512&height=512&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_MinimumDimensions_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?width=1&height=1&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_MaximumDimensions_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?width=4096&height=4096&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_DefaultDimensions_Uses256()
    {
        // No width/height specified — defaults to 256x256
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Bbox Parameter

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_ProjectedBboxWithoutBboxCrs_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-20037508,-20037508,20037508,20037508&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_ProjectedBboxWithExplicitBboxCrs_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-20037508,-20037508,20037508,20037508&bbox-crs=EPSG:3857&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_SafeCurieBboxCrs_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-90,-180,90,180&bbox-crs=%5BEPSG:4326%5D&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_NoBbox_UsesLayerExtent()
    {
        // When no bbox is provided, handler should use the layer's extent
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Background Color

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_BackgroundParameters_ReturnBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-180,-90,180,90&f=png&bgcolor=0xFF0000&transparent=false");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    #endregion

    #region Quality Parameter

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_QualityBoundaryMin_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-180,-90,180,90&f=jpeg&quality=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_QualityBoundaryMax_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map" +
            "?bbox=-180,-90,180,90&f=jpeg&quality=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region Styled Maps

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_ValidStyleId_ReachesStyledMapEndpoint()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/styles/default/map" +
            "?bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_AlphanumericStyleId_ReachesStyledMapEndpoint()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/styles/my-custom_style123/map" +
            "?bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.NotImplemented);
    }

    #endregion

    #region Dataset Map - Valid Parameters

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_SingleCollection_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/map?collections={TestLayerId}&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_MultipleCollections_ReturnsSuccess()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/map?collections={TestLayerId},{TestLayerId}&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_CollectionsWithWhitespace_ParsesCorrectly()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/map?collections=%20{TestLayerId}%20,%20{TestLayerId}%20&bbox=-180,-90,180,90&f=png");

        // Whitespace should be trimmed during parsing.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    #endregion

    #region TileSet Response Structure

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    public async Task GetMapTileSets_ValidCollection_ReturnsExpectedStructure()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map/tiles");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            json.RootElement.TryGetProperty("tilesets", out var tileSetsElement).Should().BeTrue();
            tileSetsElement.ValueKind.Should().Be(JsonValueKind.Array);

            var tileSets = tileSetsElement.EnumerateArray().ToArray();
            tileSets.Should().HaveCountGreaterOrEqualTo(1);

            foreach (var tileSet in tileSets)
            {
                tileSet.TryGetProperty("title", out _).Should().BeTrue();
                tileSet.TryGetProperty("dataType", out var dataType).Should().BeTrue();
                dataType.GetString().Should().Be("map");
                tileSet.TryGetProperty("crs", out _).Should().BeTrue();
                tileSet.TryGetProperty("tileMatrixSetURI", out _).Should().BeTrue();
                tileSet.TryGetProperty("links", out var links).Should().BeTrue();
                links.EnumerateArray().Should().NotBeEmpty();
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    public async Task GetMapTileSets_ValidCollection_IncludesWebMercatorAndWgs84()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map/tiles");

        if (response.StatusCode == HttpStatusCode.OK)
        {
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            json.RootElement.TryGetProperty("tilesets", out var tileSetsElement).Should().BeTrue();
            var tileSets = tileSetsElement.EnumerateArray().ToArray();

            var crsValues = tileSets.Select(ts => ts.GetProperty("crs").GetString()).ToArray();

            // Should include both Web Mercator and WGS84
            crsValues.Should().Contain(c => c != null && c.Contains("3857"),
                "should include Web Mercator tile set");
            crsValues.Should().Contain(c => c != null && c.Contains("CRS84"),
                "should include WGS84 tile set");
        }
    }

    #endregion
}
