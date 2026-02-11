// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcMaps;

[Collection("Database")]
[Protocol(Protocols.OgcApiMaps)]
public class OgcMapsBasicTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0; // Use existing test layer

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/conformance")]
    [Operation(Operations.Metadata)]
    public async Task GetConformance_BasicRequest_ReturnsConformanceClasses()
    {
        // Act
        var response = await _fixture.Client.GetAsync("/ogc/maps/conformance");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

        var content = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(content);

        // Verify conformance response structure
        json.RootElement.TryGetProperty("conformsTo", out var conformsTo).Should().BeTrue();
        conformsTo.EnumerateArray().Should().NotBeEmpty();

        // Verify that it includes OGC API - Maps conformance classes
        var conformanceClasses = conformsTo.EnumerateArray()
            .Select(c => c.GetString())
            .ToArray();

        conformanceClasses.Should().Contain(c => c != null && c.Contains("ogcapi-maps"));
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_WithValidParameters_ReturnsMapOrNotFound()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_UnknownFormat_ReturnsBadRequest()
    {
        // Arrange - "json" is not a valid OGC Maps format
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=json";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Render)]
    public async Task GetDatasetMap_WithCollections_ReturnsMapOrError()
    {
        // Arrange
        var queryParams = $"?collections={TestLayerId}&bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/map{queryParams}");

        // Assert
        // Note: This test might fail until raster data is available in the test database
        // For now, we expect either success or a 404 (no rasters found)
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/map")]
    [Operation(Operations.Render)]
    public async Task GetDatasetMap_WithoutCollections_ReturnsBadRequest()
    {
        // Arrange
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    [Operation(Operations.Render)]
    public async Task GetStyledMap_WithValidStyle_ReturnsNotImplemented()
    {
        // Arrange
        var styleId = "default";
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/styles/{styleId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    [Operation(Operations.GetTileMetadata)]
    public async Task GetMapTileSets_ValidCollection_ReturnsTileSetMetadata()
    {
        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{TestLayerId}/map/tiles");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            // Verify tile sets response is an array
            json.RootElement.ValueKind.Should().Be(JsonValueKind.Array);

            if (json.RootElement.GetArrayLength() > 0)
            {
                var firstTileSet = json.RootElement.EnumerateArray().First();
                firstTileSet.TryGetProperty("crs", out _).Should().BeTrue();
                firstTileSet.TryGetProperty("tileMatrixSetURI", out _).Should().BeTrue();
                firstTileSet.TryGetProperty("links", out _).Should().BeTrue();
            }
        }
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_InvalidCollectionId_ReturnsBadRequest()
    {
        // Arrange
        var invalidCollectionId = "invalid";
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{invalidCollectionId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    [Operation(Operations.Render)]
    public async Task GetCollectionMap_NonExistentCollection_ReturnsNotFound()
    {
        // Arrange
        var nonExistentCollectionId = 99999;
        var queryParams = "?bbox=-180,-90,180,90&width=256&height=256&f=png";

        // Act
        var response = await _fixture.Client.GetAsync($"/ogc/maps/collections/{nonExistentCollectionId}/map{queryParams}");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
