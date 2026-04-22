// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Server.Features.OgcTiles.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.OgcTiles;

[Protocol(Protocols.OgcApiTiles)]
[Collection("Database")]
public sealed class OgcTilesZoomLimitTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    private readonly LimitsOptions _limits = new()
    {
        Tiles = new TileLimits
        {
            MaxTileZoom = 24
        }
    };

    public async Task InitializeAsync()
    {
        _fixture.ReplaceService<IOptions<LimitsOptions>>(Options.Create(_limits));
        await _fixture.InitializeAsync();
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task GetCollectionTile_Zoom23_WithMaxTileZoom24_ReturnsTileOrNoContent()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/collections/0/tiles/WebMercatorQuad/23/0/0");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.NoContent);
        content.Contains("Invalid tile coordinates", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets/WebMercatorQuad")]
    public async Task GetWebMercatorTileMatrixSet_WithMaxTileZoom24_UsesFiniteValues()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets/WebMercatorQuad");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var definition = await response.Content.ReadFromJsonAsync<TileMatrixSetDefinition>();
        definition.Should().NotBeNull();
        definition!.TileMatrices.Should().NotBeNullOrEmpty();

        var maxMatrix = definition.TileMatrices
            .OrderByDescending(m => int.Parse(m.Id, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .First();

        maxMatrix.Id.Should().Be("24");
        double.IsFinite(maxMatrix.CellSize).Should().BeTrue();
        double.IsFinite(maxMatrix.ScaleDenominator).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /ogc/tiles/tileMatrixSets/WorldCRS84Quad")]
    public async Task GetWorldCrs84TileMatrixSet_WithMaxTileZoom24_UsesFiniteValues()
    {
        var response = await _fixture.Client.GetAsync("/ogc/tiles/tileMatrixSets/WorldCRS84Quad");
        var content = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var definition = await response.Content.ReadFromJsonAsync<TileMatrixSetDefinition>();
        definition.Should().NotBeNull();
        definition!.TileMatrices.Should().NotBeNullOrEmpty();

        var maxMatrix = definition.TileMatrices
            .OrderByDescending(m => int.Parse(m.Id, NumberStyles.Integer, CultureInfo.InvariantCulture))
            .First();

        maxMatrix.Id.Should().Be("24");
        double.IsFinite(maxMatrix.CellSize).Should().BeTrue();
        double.IsFinite(maxMatrix.ScaleDenominator).Should().BeTrue();
    }
}
