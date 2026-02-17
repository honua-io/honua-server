// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

using FluentAssertions;

using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.OgcMaps;

/// <summary>
/// Tests for OGC Maps error handling: invalid parameters, malformed input, error responses.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.OgcApiMaps)]
public class OgcMapsErrorHandlingTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private const int TestLayerId = 0;
    private const int NonExistentLayerId = 99999;

    public async Task InitializeAsync() => await _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    #region Collection ID Validation

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_NonIntegerCollectionId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/collections/not-a-number/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Collection ID must be a valid integer");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_EmptyCollectionId_ReturnsBadRequestOrNotFound()
    {
        // Empty path segment may be handled by routing
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/collections//map?f=png");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_FloatCollectionId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/collections/1.5/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Collection ID must be a valid integer");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_NonExistentCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{NonExistentLayerId}/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_InvalidBboxCrs_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?bbox=-180,-90,180,90&bbox-crs=EPSG:notvalid&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_InvalidBackgroundColor_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{TestLayerId}/map?bbox=-180,-90,180,90&bgcolor=red&transparent=false&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    public async Task GetMapTileSets_NonIntegerCollectionId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/collections/abc/map/tiles");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Collection ID must be a valid integer");
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map/tiles")]
    public async Task GetMapTileSets_NonExistentCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{NonExistentLayerId}/map/tiles");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Dataset Map - Collections Parameter

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_MissingCollections_ReturnsMapOrAccessError()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.NotFound,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_EmptyCollections_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?collections=&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_AllInvalidCollectionIds_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?collections=abc,def,ghi&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid collection IDs");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_MixedValidInvalidIds_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/map?collections=abc,{TestLayerId}&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Invalid collection IDs");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_NonExistentCollections_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/map?collections={NonExistentLayerId}&bbox=-180,-90,180,90&f=png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Styled Map Validation

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_NonIntegerCollectionId_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/collections/abc/styles/default/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("Collection ID must be a valid integer");
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/styles/{styleId}/map")]
    public async Task GetStyledMap_NonExistentCollection_ReturnsNotFound()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{NonExistentLayerId}/styles/default/map?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    #endregion

    #region Error Response Content

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/collections/{collectionId}/map")]
    public async Task GetCollectionMap_ErrorResponse_DoesNotExposeInternalDetails()
    {
        var response = await _fixture.Client.GetAsync(
            $"/ogc/maps/collections/{NonExistentLayerId}/map?f=png");

        if (response.StatusCode != HttpStatusCode.OK)
        {
            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotContain("Exception");
            content.Should().NotContain("StackTrace");
            content.Should().NotContain("ConnectionString");
            content.Should().NotContain("NpgsqlConnection");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Render)]
    [Endpoint("GET /ogc/maps/map")]
    public async Task GetDatasetMap_ErrorResponse_DoesNotExposeInternalDetails()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/maps/map?f=png");

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotContain("StackTrace");
        content.Should().NotContain("ConnectionString");
    }

    #endregion
}
