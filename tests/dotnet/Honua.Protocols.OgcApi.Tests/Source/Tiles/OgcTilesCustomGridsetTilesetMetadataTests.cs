// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Features.Tiles;
using Honua.Protocols.Ogc.Common;
using Honua.Protocols.Ogc.Api.Tiles.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Verifies that an operator-defined custom gridset is advertised as a <c>vector</c> tileset in the
/// per-dataset and per-collection OGC API tileset-metadata documents (#1916). The two built-in
/// gridsets keep their byte-identical static-descriptor path; these tests pin the additive custom
/// gridset advertisement (its own CRS/URI + full-coverage matrix limits derived from the gridset
/// geometry).
/// </summary>
[Protocol(TestProtocols.OgcApiTiles)]
[Collection("Database.OgcApiTiles")]
public sealed class OgcTilesCustomGridsetTilesetMetadataTests : IAsyncLifetime
{
    private const string CustomGridsetId = "DemoProjected3857";
    private const string CustomGridsetCrs = "http://www.opengis.net/def/crs/EPSG/0/3857";
    private const string CustomGridsetUri = "https://example.test/tilematrixset/DemoProjected3857";

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync()
    {
        var registry = new TileMatrixSetRegistry(new TileMatrixSetDefinitionOptions
        {
            Custom =
            {
                new CustomTileMatrixSet
                {
                    Id = CustomGridsetId,
                    Crs = CustomGridsetCrs,
                    Uri = CustomGridsetUri,
                    Title = "Demo Projected 3857",
                    Srid = 3857,
                    TopLeftCorner = [-20037508.342789244, 20037508.342789244],
                    TileWidth = 256,
                    TileHeight = 256,
                    Levels =
                    [
                        new TileMatrixLevel
                        {
                            Id = 0,
                            ScaleDenominator = 559082264.0287178,
                            CellSize = 156543.03392804097,
                            MatrixWidth = 1,
                            MatrixHeight = 1
                        },
                        new TileMatrixLevel
                        {
                            Id = 1,
                            ScaleDenominator = 279541132.0143589,
                            CellSize = 78271.51696402048,
                            MatrixWidth = 2,
                            MatrixHeight = 2
                        }
                    ]
                }
            }
        });

        _fixture.ReplaceService<ITileMatrixSetRegistry>(registry);
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tiles")]
    public async Task GetDatasetTilesets_IncludesCustomGridsetAsVector()
    {
        // A single-collection dataset advertises vector tiles (the multi-collection list advertises
        // a `map` composite for every gridset, built-in and custom alike — unchanged semantics).
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tiles?collections=0");

        response.Be200Ok();

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();

        var custom = tilesets!.Tilesets.SingleOrDefault(t => t.TileMatrixSetId == CustomGridsetId);
        custom.Should().NotBeNull("the custom gridset must be advertised alongside the built-ins");
        custom!.DataType.Should().Be("vector");
        custom.Crs.Should().Be(CustomGridsetCrs);
        custom.TileMatrixSetUri.Should().Be(CustomGridsetUri);
        custom.Links.Should().Contain(link => link.Rel == "item" && link.Type == MediaTypes.Mvt);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/tiles/{tileMatrixSetId}")]
    public async Task GetDatasetTileset_CustomGridset_AdvertisesVectorAndFullCoverageLimits()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/tiles/tiles/{CustomGridsetId}?collections=0");

        response.Be200Ok();

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.DataType.Should().Be("vector");
        tileset.Crs.Should().Be(CustomGridsetCrs);
        tileset.TileMatrixSetId.Should().Be(CustomGridsetId);
        tileset.TileMatrixSetUri.Should().Be(CustomGridsetUri);

        // Full-coverage limits come straight from the configured gridset geometry.
        tileset.TileMatrixSetLimits.Should().NotBeNull();
        var limits = tileset.TileMatrixSetLimits!.Value;
        limits.Should().NotBeEmpty();
        var level1 = limits.Single(limit => limit.TileMatrix == "1");
        level1.MinTileRow.Should().Be(0);
        level1.MaxTileRow.Should().Be(1);
        level1.MinTileCol.Should().Be(0);
        level1.MaxTileCol.Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}")]
    public async Task GetCollectionTileset_CustomGridset_AdvertisesVector()
    {
        var response = await _fixture.Client.GetAsync($"/ogc/tiles/collections/0/tiles/{CustomGridsetId}");

        response.Be200Ok();

        var tileset = await response.Content.ReadFromJsonAsync<TileSet>();
        tileset.Should().NotBeNull();
        tileset!.DataType.Should().Be("vector");
        tileset.Crs.Should().Be(CustomGridsetCrs);
        tileset.TileMatrixSetId.Should().Be(CustomGridsetId);
        tileset.Links.Should().Contain(link => link.Rel == "item" && link.Type == MediaTypes.Mvt);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles")]
    public async Task GetCollectionTilesets_IncludesCustomGridsetAsVector()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles");

        response.Be200Ok();

        var tilesets = await response.Content.ReadFromJsonAsync<TileSetsList>();
        tilesets.Should().NotBeNull();

        var custom = tilesets!.Tilesets.SingleOrDefault(t => t.TileMatrixSetId == CustomGridsetId);
        custom.Should().NotBeNull();
        custom!.DataType.Should().Be("vector");
        custom.Crs.Should().Be(CustomGridsetCrs);
    }
}
