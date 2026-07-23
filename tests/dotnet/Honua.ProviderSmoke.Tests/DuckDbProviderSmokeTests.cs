// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Interface-level HTTP-stack smoke coverage for the DuckDB provider (honua-server#2947).
/// Boots a real ASP.NET Core host with <c>DataSource:Provider=duckdb</c> against a
/// standalone, file-backed DuckDB database. See <see cref="PrimaryProviderSmokeTestsBase"/>
/// for the shared assertions.
/// </summary>
[Trait("Provider", "DuckDb")]
public sealed class DuckDbProviderSmokeTests : PrimaryProviderSmokeTestsBase, IClassFixture<DuckDbProviderWebAppFixture>
{
    private readonly DuckDbProviderWebAppFixture _fixture;

    public DuckDbProviderSmokeTests(DuckDbProviderWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    protected override HttpClient Client => _fixture.Client;

    // Not in the shared base (see PrimaryProviderSmokeTestsBase's comment): MySQL fails
    // this due to a real product bug (honua-server#2965), so each concrete subclass
    // declares its own copy rather than the base sharing a same-named [Fact] xunit's
    // analyzer would forbid hiding.
    [IntegrationTest]
    [Protocol(ProtocolNames.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_QueryWithBbox_ReturnsWindowedFeatures()
    {
        var (west, south, east, north) = ProviderSmokeData.NarrowBbox;
        var response = await Client.GetAsync(
            $"/rest/services/{ProviderSmokeGraph.ServiceName}/FeatureServer/{ProviderSmokeGraph.LayerId}/query" +
            $"?where=1%3D1&geometry={west},{south},{east},{north}&geometryType=esriGeometryEnvelope" +
            "&inSR=4326&spatialRel=esriSpatialRelIntersects&outFields=*&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Should().HaveCount(ProviderSmokeData.NarrowBboxCount);
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.OgcApiTiles)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task Tiles_RasterTile_ReturnsNonEmptyPng()
    {
        // WorldCRS84Quad (native EPSG:4326, matching the seeded layer's storage SRID), not
        // WebMercatorQuad (EPSG:3857): a WebMercatorQuad tile request would require the
        // provider to reproject 4326 storage geometry into 3857 tile space. WorldCRS84Quad
        // needs no reprojection. Zoom 0 / row 0 / col 0 covers the western hemisphere
        // (-180..0 longitude), so the seeded parcels (~-122 longitude) always fall inside it.
        var response = await Client.GetAsync(
            $"/ogc/tiles/collections/{ProviderSmokeGraph.LayerId}/tiles/WorldCRS84Quad/0/0/0?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }
}
