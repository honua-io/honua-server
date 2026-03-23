// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Ogc.Common;
using Honua.Server.Features.OgcTiles.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.OgcTiles;

[Protocol(Protocols.OgcApiTiles)]
[Collection("Database")]
public sealed class OgcTilesCrsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task GetTilesets_IncludesBothTileMatrixSets()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();
        tilesets!.Tilesets.Should().NotBeEmpty();

        var tmsIds = tilesets.Tilesets.Select(t => t.TileMatrixSetId).Distinct().ToArray();
        tmsIds.Should().Contain("WebMercatorQuad");
        tmsIds.Should().Contain("WorldCRS84Quad");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WorldCRS84Quad_ReturnsTile()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/tiles/WorldCRS84Quad/0/0/0?collections=0");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Mvt);
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets")]
    public async Task GetTileMatrixSets_IncludesBothSets()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var list = await response.Content.ReadFromJsonAsync<TileMatrixSetsList>();
        list.Should().NotBeNull();
        list!.TileMatrixSets.Should().Contain(item => item.Id == "WebMercatorQuad");
        list.TileMatrixSets.Should().Contain(item => item.Id == "WorldCRS84Quad");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets/{tileMatrixSetId}")]
    public async Task GetTileMatrixSet_WorldCRS84Quad_ReturnsDefinition()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets/WorldCRS84Quad");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var definition = await response.Content.ReadFromJsonAsync<TileMatrixSetDefinition>();
        definition.Should().NotBeNull();
        definition!.Id.Should().Be("WorldCRS84Quad");
        definition.Crs.Should().Be("http://www.opengis.net/def/crs/EPSG/0/4326");
        definition.TileMatrices.Should().NotBeEmpty();

        // At zoom 0, WorldCRS84Quad has 2 columns x 1 row
        var zoom0 = definition.TileMatrices.First(m => m.Id == "0");
        zoom0.MatrixWidth.Should().Be(2);
        zoom0.MatrixHeight.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_WorldCRS84Quad_ReturnsTilesetMetadata()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/tiles/WorldCRS84Quad?collections=0");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.TileMatrixSetId.Should().Be("WorldCRS84Quad");
        tileset.Crs.Should().Be("http://www.opengis.net/def/crs/EPSG/0/4326");
        tileset.MediaTypes.Should().NotBeNull();
        tileset.MediaTypes!.Value.Should().Contain(MediaTypes.Mvt);
        tileset.MediaTypes!.Value.Should().Contain(MediaTypes.Png);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}")]
    public async Task GetCollectionTileset_WorldCRS84Quad_ReturnsTilesetMetadata()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WorldCRS84Quad");

        response.Be200Ok();
        response.Content.Headers.ContentType?.MediaType.Should().Be(MediaTypes.Json);

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.TileMatrixSetId.Should().Be("WorldCRS84Quad");
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WorldCRS84Quad_WithCrs4326_ReturnsTile()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WorldCRS84Quad/0/0/0?crs=EPSG:4326");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
    }

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetTile_WorldCRS84Quad_WithInvalidCrs_ReturnsBadRequest()
    {
        var response = await _fixture.Client.GetAsync(
            "/ogc/tiles/collections/0/tiles/WorldCRS84Quad/0/0/0?crs=EPSG:3857");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/conformance")]
    public async Task GetConformance_IncludesPngConformanceClass()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/conformance");

        response.Be200Ok();

        var conformance = await response.Content.ReadFromJsonAsync<ConformanceDeclaration>();
        conformance.Should().NotBeNull();

        var classes = conformance!.ConformsTo.ToArray();
        classes.Should().Contain("http://www.opengis.net/spec/ogcapi-tiles-1/1.0/conf/png");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}")]
    public async Task GetCollection_SpatialExtent_UsesCrs84()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0");

        response.Be200Ok();

        var collection = await response.Content.ReadFromJsonAsync<CollectionInfo>();
        collection.Should().NotBeNull();

        if (collection!.Extent?.Spatial != null)
        {
            collection.Extent.Spatial.Crs.Should().Be("http://www.opengis.net/def/crs/OGC/1.3/CRS84");

            var bbox = collection.Extent.Spatial.BoundingBox;
            bbox.Should().NotBeEmpty();
            // CRS84 lon range: -180..180, lat range: -90..90
            bbox[0][0].Should().BeInRange(-180, 180); // minLon
            bbox[0][1].Should().BeInRange(-90, 90);   // minLat
            bbox[0][2].Should().BeInRange(-180, 180); // maxLon
            bbox[0][3].Should().BeInRange(-90, 90);   // maxLat
        }
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles")]
    public async Task GetCollectionTilesets_IncludesBothTileMatrixSets()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles");

        response.Be200Ok();

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();

        var tmsIds = tilesets!.Tilesets.Select(t => t.TileMatrixSetId).Distinct().ToArray();
        tmsIds.Should().Contain("WebMercatorQuad");
        tmsIds.Should().Contain("WorldCRS84Quad");
    }
}
