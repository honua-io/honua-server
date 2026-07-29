// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
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
/// <para>
/// The FeatureServer where/bbox, OGC API Features items, OData, and raster-tile cases
/// exercise real, working end-to-end coverage: reads route to SQL Server through
/// <c>FeatureProviderQueryRouter</c>/<c>TileFeatureProviderResolver</c> (honua-server#2962),
/// and the bbox/envelope path works since the honua-server#2965 EWKB/plain-WKB converter
/// fix. Two cases remain intentionally not-green, each with a specific reason rather than
/// silently omitting the coverage:
/// <list type="bullet">
///   <item>CQL2 <c>filter=</c> (OGC API Features) — still-real, documented limitation
///   (sql-server.md's WHERE Clause section): no SQL Server <c>ISqlFilterTranslator</c>
///   exists, unaffected by #2965/#2962. See the skip reason.</item>
///   <item>OGC API Tiles vector (MVT) — not a routing gap: native MVT generation is a
///   per-provider capability that only the PostGIS provider implements, so a
///   SQL-Server-backed collection returns <c>501 Not Implemented</c> for vector tiles
///   regardless of routing.</item>
/// </list>
/// </para>
/// <para>
/// Write posture: SQL Server is a read/query-only additional provider
/// (<c>SqlServerFeatureStore.Writer</c> is <see langword="null"/>,
/// <c>FeatureProviderEditCapabilities.ReadOnly</c>; sql-server.md documents "no edits").
/// The edit round-trip test proves the documented fail-closed rejection:
/// <c>ODataFeatureProviderResolver.CheckWriteSupportAsync</c> refuses writes for layers
/// routed to a secondary provider with a clean <c>501 ProviderWriteNotSupported</c>
/// OData error — never a 500, and never a silent write into the primary provider.
/// </para>
/// </remarks>
[Trait("Provider", "SqlServer")]
public sealed class SqlServerProviderSmokeTests : IClassFixture<SqlServerProviderWebAppFixture>
{
    private const string Cql2FilterUnsupportedReason =
        "Still-real, documented limitation re-confirmed after the #2965/#2962 fixes landed " +
        "(sql-server.md's WHERE Clause section): no SQL Server ISqlFilterTranslator is " +
        "registered, so the shared CQL2 pipeline either produces a Postgres-flavored " +
        "SqlFilter that SqlServerFeatureQueryBuilder explicitly rejects with " +
        "NotSupportedException, or fails translation outright. Neither #2965 (EWKB " +
        "converter) nor #2962 (query routing) touched filter translation. FeatureServer's " +
        "where= (canonical Where text) path works and is covered by " +
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
        // Enabled since the honua-server#2965 EWKB/plain-WKB converter fix landed: filter
        // geometries are now translated to the plain WKB flavor SQL Server's
        // geometry::STGeomFromWKB(wkb, srid) expects (previously the embedded SRID header
        // corrupted ring/point-count parsing and every bbox query 500'd).
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

        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Length.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.ODataV4)]
    [Operation(Operations.Create)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public async Task OData_CreateUpdateDelete_SecondaryReadOnlyProvider_Returns501AndLeavesDataUnchanged()
    {
        // SQL Server is a read/query-only additional provider (Writer is null,
        // FeatureProviderEditCapabilities.ReadOnly). The OData adapter's write-support
        // guard (ODataFeatureProviderResolver.CheckWriteSupportAsync) must fail closed
        // for layers routed to a secondary provider: a clean 501 ProviderWriteNotSupported
        // for create, update, and delete — never a 500, and never a write applied to the
        // primary (Postgres) provider for a SQL-Server-backed layer.
        using var createContent = new StringContent(
            /*lang=json,strict*/ """{"Attributes":{"name":"Should Not Exist","type":"commercial"}}""",
            Encoding.UTF8,
            "application/json");
        var createResponse = await Client.PostAsync(
            $"/odata/Layers({ProviderSmokeGraph.LayerId})/Features",
            createContent);
        await AssertProviderWriteNotSupportedAsync(createResponse);

        using var updateContent = new StringContent(
            /*lang=json,strict*/ """{"Attributes":{"name":"Should Not Change"}}""",
            Encoding.UTF8,
            "application/json");
        using var updateRequest = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/odata/Layers({ProviderSmokeGraph.LayerId})/Features(1)")
        {
            Content = updateContent,
        };
        var updateResponse = await Client.SendAsync(updateRequest);
        await AssertProviderWriteNotSupportedAsync(updateResponse);

        var deleteResponse = await Client.DeleteAsync(
            $"/odata/Layers({ProviderSmokeGraph.LayerId})/Features(2)");
        await AssertProviderWriteNotSupportedAsync(deleteResponse);

        // Round-trip proof: the rejected mutations changed nothing in the SQL
        // Server-backed layer — all 5 seeded rows are still served.
        var readResponse = await Client.GetAsync($"/odata/Features({ProviderSmokeGraph.LayerId})");
        readResponse.StatusCode.Should().Be(HttpStatusCode.OK, await readResponse.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await readResponse.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("value").GetArrayLength().Should().Be(ProviderSmokeData.Parcels.Count);
    }

    private static async Task AssertProviderWriteNotSupportedAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, body);
        body.Should().Contain("ProviderWriteNotSupported");
        // Sanitized rejection — no provider internals, SQL, or stack traces.
        body.Should().NotContainAny("Exception", "SqlClient", "stack");
    }
}
