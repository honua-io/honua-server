// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Interface-level HTTP-stack smoke coverage for the SQL Server secondary/additional
/// provider (honua-server#2947). Boots a Postgres-primary <see cref="Honua.TestKit.WebAppFixture"/>
/// with a Testcontainers <c>mcr.microsoft.com/mssql/server:2022-latest</c> instance
/// registered as a secure connection routed through <c>FeatureProviderQueryRouter</c>,
/// replacing the creds-gated <c>HONUA_SQLSERVER_TEST_CONNECTION</c> approach for this suite
/// (the existing creds-gated tests in <c>Honua.SqlServer.Tests</c> are untouched).
/// </summary>
/// <remarks>
/// <see cref="FeatureServer_QueryWithWhereClause_ReturnsFilteredFeatures"/>,
/// <see cref="OgcFeatures_Items_ReturnsAllSeededFeatures"/>,
/// <see cref="OData_Features_ReturnsAllSeededFeatures"/>, and
/// <see cref="Tiles_RasterTile_ReturnsNonEmptyPng"/> exercise real, working end-to-end
/// coverage (the OData and Tiles cases route to SQL Server through
/// <c>FeatureProviderQueryRouter</c>/<c>TileFeatureProviderResolver</c>,
/// honua-server#2962). CQL2 <c>filter=</c> remains skipped because the shared SQL
/// filter translator currently supports PostgreSQL only. OGC API Tiles vector (MVT)
/// remains a separate provider capability that only PostGIS implements.
/// </remarks>
[Trait("Provider", "SqlServer")]
public sealed class SqlServerProviderSmokeTests : IClassFixture<SqlServerProviderWebAppFixture>
{
    private const string Cql2FilterUnsupportedReason =
        "Pre-existing, already-documented limitation (sql-server.md's WHERE Clause section): " +
        "the shared ISqlFilterTranslator pipeline only registers a PostgreSQL translator, so " +
        "CQL2/OGC API Features filter=... throws NotSupportedException for SQL Server. " +
        "FeatureServer's where= (canonical Where text) path works and is covered by " +
        "FeatureServer_QueryWithWhereClause_ReturnsFilteredFeatures.";

    private readonly SqlServerProviderWebAppFixture _fixture;

    public SqlServerProviderSmokeTests(SqlServerProviderWebAppFixture fixture)
    {
        _fixture = fixture;
    }

    private HttpClient Client => _fixture.Client;

    [IntegrationTest]
    [Protocol(ProtocolNames.FeatureServer)]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{id}/FeatureServer/{layerId}/query")]
    public async Task FeatureServer_QueryWithWhereClause_ReturnsFilteredFeatures()
    {
        var response = await Client.GetAsync(
            $"/rest/services/{ProviderSmokeGraph.ServiceName}/FeatureServer/{ProviderSmokeGraph.LayerId}/query" +
            $"?where={Uri.EscapeDataString("type='commercial'")}&outFields=*&f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Should().HaveCount(ProviderSmokeData.CommercialCount);
        foreach (var feature in features)
        {
            feature.GetProperty("attributes").GetProperty("type").GetString().Should().Be("commercial");
        }
    }

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
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(ProviderSmokeData.NarrowBboxCount);
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.OgcApiFeatures)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeatures_Items_ReturnsAllSeededFeatures()
    {
        var response = await Client.GetAsync($"/ogc/features/collections/{ProviderSmokeGraph.LayerId}/items");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        document.RootElement.GetProperty("features").GetArrayLength().Should().Be(ProviderSmokeData.Parcels.Count);
    }

    [Fact(Skip = Cql2FilterUnsupportedReason)]
    [Trait("Category", "Integration")]
    [Protocol(ProtocolNames.OgcApiFeatures)]
    [Operation(Operations.CqlFilter)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public Task OgcFeatures_ItemsWithCql2Filter_NotSupportedForSqlServer() => Task.CompletedTask;

    [IntegrationTest]
    [Protocol(ProtocolNames.ODataV4)]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features({layerId})")]
    public async Task OData_Features_ReturnsAllSeededFeatures()
    {
        var response = await Client.GetAsync($"/odata/Features({ProviderSmokeGraph.LayerId})");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("value").GetArrayLength().Should().Be(ProviderSmokeData.Parcels.Count);
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task OData_CreateFeature_ReadOnlyProvider_ReturnsProviderWriteNotSupported()
    {
        using var content = JsonContent.Create(new
        {
            Attributes = new Dictionary<string, object?>
            {
                ["name"] = "Must not persist",
                ["area"] = 999.0,
                ["type"] = "commercial"
            }
        });

        var response = await Client.PostAsync(
            $"/odata/Layers({ProviderSmokeGraph.LayerId})/Features",
            content);

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, await response.Content.ReadAsStringAsync());
        using var errorDocument = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        errorDocument.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("ProviderWriteNotSupported");

        var queryResponse = await Client.GetAsync($"/odata/Features({ProviderSmokeGraph.LayerId})");
        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK, await queryResponse.Content.ReadAsStringAsync());
        using var queryDocument = JsonDocument.Parse(await queryResponse.Content.ReadAsStringAsync());
        queryDocument.RootElement.GetProperty("value").GetArrayLength().Should().Be(ProviderSmokeData.Parcels.Count);
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
        // PNG (not the MVT default): raster tiles route to SQL Server through
        // FeatureProviderQueryRouter (honua-server#2962); vector/MVT tiles remain a separate,
        // pre-existing capability gap (no provider besides PostGIS implements ITileProvider).
        var response = await Client.GetAsync(
            $"/ogc/tiles/collections/{ProviderSmokeGraph.LayerId}/tiles/WorldCRS84Quad/0/0/0?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().StartWith([0x89, 0x50, 0x4E, 0x47]);
    }
}
