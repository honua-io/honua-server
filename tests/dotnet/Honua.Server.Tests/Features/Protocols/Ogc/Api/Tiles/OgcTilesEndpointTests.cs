// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.Catalog.Domain;
using Honua.Server.Features.Protocols.Ogc.Common;
using Honua.Server.Features.Protocols.Ogc.Api.Tiles.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

[Protocol(TestProtocols.OgcApiTiles)]
[Collection("Database")]
public sealed class OgcTilesEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles")]
    public async Task GetLandingPage_ReturnsRequiredLinks()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var landingPage = await response.Content.ReadFromJsonAsync<LandingPage>();
        landingPage.Should().NotBeNull();
        landingPage!.Supports3d.Should().BeFalse();
        landingPage!.Links.Should().NotBeEmpty();

        var links = landingPage.Links.ToArray();
        links.Should().Contain(l => l.Rel == RelationTypes.Self);
        links.Should().Contain(l => l.Rel == RelationTypes.ServiceDesc);
        links.Should().Contain(l => l.Rel == RelationTypes.Conformance);
        links.Should().Contain(l => l.Rel == RelationTypes.TilesetsVector);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/conformance")]
    public async Task GetConformance_ReturnsTilesConformanceClasses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/conformance");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var conformance = await response.Content.ReadFromJsonAsync<ConformanceDeclaration>();
        conformance.Should().NotBeNull();

        var classes = conformance!.ConformsTo.ToArray();
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/core");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tilesets-list");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/tileset");
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/mvt");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/openapi.json")]
    public async Task GetOpenApi_ReturnsJson()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/openapi.json");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().StartWith("application/vnd.oai.openapi+json");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/openapi.json")]
    public async Task GetOpenApi_DocumentsSecuritySchemesAndProtectedResponses()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/openapi.json");

        response.Be200Ok();

        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var securitySchemes = json.RootElement
            .GetProperty("components")
            .GetProperty("securitySchemes");
        securitySchemes.TryGetProperty("ApiKeyAuth", out _).Should().BeTrue();
        securitySchemes.TryGetProperty("BearerAuth", out _).Should().BeTrue();

        var collections = json.RootElement.GetProperty("paths")
            .GetProperty("/collections")
            .GetProperty("get")
            .GetProperty("responses");
        collections.TryGetProperty("401", out _).Should().BeTrue();
        collections.TryGetProperty("403", out _).Should().BeTrue();

        var datasetTile = json.RootElement.GetProperty("paths")
            .GetProperty("/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")
            .GetProperty("get");
        datasetTile.TryGetProperty("security", out var security).Should().BeTrue();
        security.ValueKind.Should().Be(JsonValueKind.Array);
        datasetTile.GetProperty("responses").TryGetProperty("401", out _).Should().BeTrue();
        datasetTile.GetProperty("responses").TryGetProperty("403", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections")]
    public async Task GetCollections_ReturnsCollections()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var collections = await response.Content.ReadFromJsonAsync<Collections>();
        collections.Should().NotBeNull();
        collections!.CollectionList.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections")]
    public async Task GetCollections_ProtocolDisabled_ReturnsNotFound()
    {
        await UpdateServiceProtocolsAsync();

        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task GetCollection_ReturnsCollectionMetadata()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var collection = await response.Content.ReadFromJsonAsync<CollectionInfo>();
        collection.Should().NotBeNull();
        collection!.Links.Should().Contain(l => l.Rel == RelationTypes.TilesetsVector);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task GetCollection_ProtocolDisabled_ReturnsNotFound()
    {
        await UpdateServiceProtocolsAsync();

        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task GetCollection_WithTemporalFields_AdvertisesTemporalExtent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0");

        response.Be200Ok();

        var collection = await response.Content.ReadFromJsonAsync<CollectionInfo>();
        collection.Should().NotBeNull();
        collection!.Extent.Should().NotBeNull();
        collection.Extent!.Temporal.Should().NotBeNull();
        collection.Extent.Temporal!.Interval.Should().NotBeEmpty();
        collection.Extent.Temporal.Interval[0].Length.Should().Be(2);
        collection.Extent.Temporal.Interval[0][0].Should().Be("2022-12-31T23:00:00Z");
        collection.Extent.Temporal.Interval[0][1].Should().Be("2024-10-15T00:00:00Z");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task GetDatasetTilesets_ReturnsTilesetsList()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();
        tilesets!.Tilesets.Should().NotBeEmpty();
        tilesets.Tilesets.First().DataType.Should().Be("map");
        tilesets.Tilesets.First().Links.Should().Contain(l => l.Rel == RelationTypes.TilingScheme);
        tilesets.Tilesets.First().Links.Should().Contain(l => l.Rel == "item" && l.Type == MediaTypes.Png);
        tilesets.Tilesets.First().Links.Should().NotContain(l => l.Rel == "item" && l.Type == MediaTypes.Mvt);
        tilesets.Tilesets.First().Links.Should().NotContain(l => l.Href.Contains("collections=", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task GetDatasetTilesets_ProtocolDisabled_ReturnsNotFound()
    {
        await UpdateServiceProtocolsAsync();

        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task GetDatasetTilesets_WithCollections_CanonicalizesOrderInLinks()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles?collections=1,0");

        response.Be200Ok();

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();
        tilesets!.Tilesets.Should().NotBeEmpty();
        tilesets.Tilesets.First().DataType.Should().Be("map");
        tilesets.Tilesets.SelectMany(tileset => tileset.Links).Should().Contain(
            link => link.Href.Contains("collections=0,1", StringComparison.OrdinalIgnoreCase) &&
                    link.Href.Contains("f=png", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_ReturnsTilesetMetadata()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad?collections=0");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.DataType.Should().Be("vector");
        tileset!.Links.Should().Contain(l => l.Rel == RelationTypes.TilingScheme);
        tileset.Links.Should().Contain(l => l.Rel == "item" && l.Type == MediaTypes.Mvt);
        tileset.Links.Should().NotContain(l => l.Rel == "item" && l.Type == MediaTypes.Png);
        tileset.MediaTypes.Should().NotBeNull();
        tileset.MediaTypes!.Value.Should().Contain(MediaTypes.Mvt);
        tileset.MediaTypes!.Value.Should().Contain(MediaTypes.Png);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_WithMalformedCollectionsDelimiter_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad?collections=0,");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_WithoutCollections_WhenMultipleCollectionsExist_ReturnsTilesetMetadata()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.DataType.Should().Be("map");
        tileset!.Links.Should().Contain(l => l.Rel == RelationTypes.TilingScheme);
        tileset.Links.Should().Contain(l => l.Rel == "item" && l.Type == MediaTypes.Png);
        tileset.Links.Should().NotContain(l => l.Rel == "item" && l.Type == MediaTypes.Mvt);
        tileset.MediaTypes.Should().NotBeNull();
        tileset.MediaTypes!.Value.Should().Equal(MediaTypes.Png);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_WithMultipleCollections_CanonicalizesLinksAndUsesDatasetMetadataLink()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad?collections=1,0");

        response.Be200Ok();

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.DataType.Should().Be("map");
        tileset.Links.Should().Contain(
            link => link.Rel == "item" &&
                    link.Type == MediaTypes.Png &&
                    link.Href.Contains("collections=0,1", StringComparison.OrdinalIgnoreCase) &&
                    link.Href.Contains("f=png", StringComparison.OrdinalIgnoreCase));
        tileset.Links.Should().NotContain(link => link.Rel == "item" && link.Type == MediaTypes.Mvt);
        tileset.Links.Should().Contain(link => link.Rel == RelationTypes.Geodata && link.Href.EndsWith("/ogc/features/collections", StringComparison.OrdinalIgnoreCase));
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles")]
    public async Task GetCollectionTilesets_ReturnsTilesetsList()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();
        tilesets!.Tilesets.Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}")]
    public async Task GetCollectionTileset_ReturnsTilesetMetadata()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles/WebMercatorQuad");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.Links.Should().Contain(l => l.Rel == RelationTypes.TilingScheme);
        tileset.Links.Should().Contain(l => l.Rel == "item");

        var tileLimits = _fixture.Services.GetRequiredService<IOptions<LimitsOptions>>().Value.Tiles;
        var minZoom = Math.Max(0, tileLimits.MinTileZoom);
        var maxZoom = Math.Max(minZoom, tileLimits.MaxTileZoom);
        tileset.TileMatrixSetLimits.Should().NotBeNull();
        tileset.TileMatrixSetLimits!.Value.Should().HaveCount(maxZoom - minZoom + 1);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_ReturnsMvtOrNoContent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Mvt);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WithUnsupportedCrs_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0?crs=EPSG:4326");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}")]
    public async Task GetTile_WithSubset_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0?subset=E(0:1)");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow:int}/{tileCol:int}")]
    public async Task GetTile_WithDatetime_ReturnsMvtOrNoContent()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WebMercatorQuad/0/0/0?datetime=2023-01-02T00:00:00Z");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetDatasetTile_ReturnsMvtOrNoContent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad/0/0/0?collections=0");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Mvt);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetDatasetTile_WithoutCollections_WhenMultipleCollectionsExist_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad/0/0/0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetDatasetTile_WithMultipleCollections_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles/WebMercatorQuad/0/0/0?collections=1,0");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets")]
    public async Task GetTileMatrixSets_ReturnsWebMercatorQuad()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var list = await response.Content.ReadFromJsonAsync<TileMatrixSetsList>();
        list.Should().NotBeNull();
        list!.TileMatrixSets.Should().Contain(item => item.Id == "WebMercatorQuad");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets/{tileMatrixSetId}")]
    public async Task GetTileMatrixSet_ReturnsDefinition()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets/WebMercatorQuad");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var definition = await response.Content.ReadFromJsonAsync<TileMatrixSetDefinition>();
        definition.Should().NotBeNull();
        definition!.TileMatrices.Should().NotBeEmpty();
    }

    private Task UpdateServiceProtocolsAsync()
    {
        // V2 cutover (#1035 72/N): protocol gating reads MetadataV2Service.Protocols.
        // Seed the in-memory test graph directly via the fixture helper.
        _fixture.UpdateV2ServiceMetadata(
            WebAppFixture.TestServiceId,
            enabledProtocols: ServiceProtocols.All
                .Where(protocol => !string.Equals(protocol, ServiceProtocols.OgcApiTiles, StringComparison.Ordinal))
                .ToArray());
        return Task.CompletedTask;
    }
}
