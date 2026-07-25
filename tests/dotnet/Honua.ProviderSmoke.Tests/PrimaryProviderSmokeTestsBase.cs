// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.ProviderSmoke.Tests;

/// <summary>
/// Shared interface-level smoke assertions run against every primary-provider
/// (DuckDB/MySql) web-app fixture (honua-server#2947). Both providers are primary
/// <c>DataSource:Provider</c> replacements, so the seeded "parcels" layer
/// (<see cref="ProviderSmokeGraph.LayerId"/>, see <see cref="ProviderSmokeData"/>) is
/// reachable through every read/query GA
/// surface it documents support for: GeoServices FeatureServer, OGC API Features,
/// OData, and tiles (TileJSON + a raster PNG tile; vector MVT is out of scope —
/// both providers' <c>FeatureProviderCapabilities.SupportsNativeMvt</c> is
/// <see langword="false"/> by design, see docs/reference/configuration/data-sources).
/// </summary>
/// <remarks>
/// xunit runs <c>[Fact]</c>-attributed methods declared on a base class once per
/// concrete subclass, so this base class's test bodies execute independently
/// against the DuckDB fixture (<see cref="DuckDbProviderSmokeTests"/>) and the
/// MySql fixture (<see cref="MySqlProviderSmokeTests"/>) — real, separate test
/// results per provider, not a single shared run.
/// </remarks>
public abstract class PrimaryProviderSmokeTestsBase
{
    /// <summary>HTTP client bound to the concrete subclass's provider-backed host.</summary>
    protected abstract HttpClient Client { get; }

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
        document.RootElement.GetProperty("features").GetArrayLength()
            .Should().Be(ProviderSmokeData.NarrowBboxCount);
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

    [IntegrationTest]
    [Protocol(ProtocolNames.OgcApiFeatures)]
    [Operation(Operations.CqlFilter)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task OgcFeatures_ItemsWithCql2Filter_ReturnsFilteredFeatures()
    {
        var filter = "type = 'commercial'";
        var response = await Client.GetAsync(
            $"/ogc/features/collections/{ProviderSmokeGraph.LayerId}/items?filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("features").EnumerateArray().ToArray();

        features.Should().HaveCount(ProviderSmokeData.CommercialCount);
        foreach (var feature in features)
        {
            feature.GetProperty("properties").GetProperty("type").GetString().Should().Be("commercial");
        }
    }

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
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=name eq 'value'")]
    public async Task OData_FeaturesWithFilter_ReturnsFilteredFeatures()
    {
        var filter = "type eq 'commercial'";
        var response = await Client.GetAsync(
            $"/odata/Features({ProviderSmokeGraph.LayerId})?$filter={Uri.EscapeDataString(filter)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("value").EnumerateArray().ToArray();

        features.Should().HaveCount(ProviderSmokeData.CommercialCount);
        foreach (var feature in features)
        {
            var attrs = Honua.TestKit.Helpers.ODataTestHelpers.ParseAttributes(feature);
            attrs.GetProperty("type").GetString().Should().Be("commercial");
        }
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.OgcApiTiles)]
    [Operation(Operations.GetTileMetadata)]
    [Endpoint("GET /tiles/{layerId}/tile.json")]
    public async Task Tiles_TileJson_ReturnsMetadata()
    {
        var response = await Client.GetAsync($"/tiles/{ProviderSmokeGraph.LayerId}/tile.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Protocol(ProtocolNames.OgcApiTiles)]
    [Operation(Operations.GetTile)]
    [Endpoint("GET /ogc/tiles/collections/{collectionId}/tiles/{tileMatrixSetId}/{tileMatrix}/{tileRow}/{tileCol}")]
    public async Task Tiles_RasterTile_ReturnsNonEmptyPng()
    {
        var response = await Client.GetAsync(
            $"/ogc/tiles/collections/{ProviderSmokeGraph.LayerId}/tiles/WorldCRS84Quad/0/0/0?f=png");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Content.Headers.ContentType?.MediaType.Should().Be("image/png");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().StartWith([0x89, 0x50, 0x4E, 0x47]);
    }
}
