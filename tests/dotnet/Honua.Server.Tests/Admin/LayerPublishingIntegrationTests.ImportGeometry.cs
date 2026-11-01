// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData("Polygon", "POLYGON", "Polygon", "esriGeometryPolygon", 4326)]
    [InlineData("PointCsv", "POINT", "Point", "esriGeometryPoint", 4326)]
    [InlineData("PointCsv", "POINT", "Point", "esriGeometryPoint", 3857)]
    [InlineData("Mixed", "GEOMETRY", "GeometryCollection", null, 4326)]
    [InlineData("Point", "POINT", "Point", "esriGeometryPoint", 4326)]
    [InlineData("MultiPoint", "MULTIPOINT", "MultiPoint", "esriGeometryMultipoint", 4326)]
    [InlineData("LineString", "LINESTRING", "LineString", "esriGeometryPolyline", 4326)]
    [InlineData("MultiLineString", "MULTILINESTRING", "MultiLineString", "esriGeometryPolyline", 4326)]
    [InlineData("MultiPolygon", "MULTIPOLYGON", "MultiPolygon", "esriGeometryPolygon", 4326)]
    [Protocol(TestProtocols.OgcApiFeatures, TestProtocols.FeatureServer, TestProtocols.Mcp)]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("POST /mcp tools/call honua_list_layers")]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    public async Task PublishImportedGeometry_DiscoveredType_MatchesAllCatalogsAndFeatures(
        string scenario, string databaseType, string canonicalType, string? esriType, int targetSrid)
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var publishConnection = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(publishConnection.ConnectionString);

        const string polygon = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""";
        const string point = """{"type":"Point","coordinates":[0.5,0.5]}""";
        var geometry = scenario switch
        {
            "Polygon" or "Mixed" => polygon,
            "MultiPolygon" => """{"type":"MultiPolygon","coordinates":[[[[0,0],[1,0],[1,1],[0,0]]]]}""",
            "MultiPoint" => """{"type":"MultiPoint","coordinates":[[0,0],[1,1]]}""",
            "LineString" => """{"type":"LineString","coordinates":[[0,0],[1,1]]}""",
            "MultiLineString" => """{"type":"MultiLineString","coordinates":[[[0,0],[1,1]]]}""",
            _ => point
        };
        var secondGeometry = scenario == "Mixed" ? point : geometry;
        var source = scenario == "PointCsv"
            ? "name,population,longitude,latitude\nAlpha,2,0.5,0.5\nBravo,10,1,1\n"
            : $$"""
              { "type": "FeatureCollection", "features": [
                { "type": "Feature", "geometry": {{geometry}}, "properties": { "name": "Alpha", "population": 2 } },
                { "type": "Feature", "geometry": {{secondGeometry}}, "properties": { "name": "Bravo", "population": 10 } }
              ] }
              """;
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(source), "File", scenario == "PointCsv" ? "points.csv" : "features.geojson");
        upload.Add(new StringContent($"geometry_{Guid.NewGuid():N}"), "TableName");
        upload.Add(new StringContent(targetSrid.ToString(System.Globalization.CultureInfo.InvariantCulture)), "TargetSrid");
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        var imported = JsonSerializer.Deserialize<ImportResult>(payload, _jsonOptions)!;
        imported.Success.Should().BeTrue(payload);
        imported.FeatureCount.Should().Be(2);
        _importedTableSchema = imported.Schema;
        _importedTableName = imported.PhysicalTableName;
        imported.TypedColumns.Should().Contain(column => column.Name == "population" && column.PostgresType == "bigint");

        using var discovery = await GetGeometryJsonAsync($"/api/v1/admin/connections/{_connectionId}/tables");
        var table = discovery.RootElement.GetProperty("tables").EnumerateArray().Single(table =>
            table.GetProperty("schema").GetString() == imported.Schema &&
            table.GetProperty("table").GetString() == imported.PhysicalTableName);
        table.GetProperty("geometryType").GetString().Should().Be(databaseType);
        table.GetProperty("srid").GetInt32().Should().Be(targetSrid);

        // Quick publication must discover the authoritative type without a client override.
        var publishRequest = new PublishLayerRequest
        {
            Schema = imported.Schema!, Table = imported.PhysicalTableName!,
            LayerName = $"Geometry {scenario}", PrimaryKey = "id",
            Fields = ["id", "name", "population", "properties"], ServiceName = _serviceName, Enabled = true
        };
        if (scenario == "Mixed")
        {
            var rejected = await PostPublishLayerAsync(publishRequest);
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            rejected.Payload.Should().Contain("mixed-geometry");
            using var service = await _client.GetAsync($"/rest/services/{_serviceName}/FeatureServer?f=json");
            service.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var collections = await GetGeometryJsonAsync("/ogc/features/collections?f=json");
            collections.RootElement.GetProperty("collections").EnumerateArray().Should().NotContain(
                collection => collection.GetProperty("title").GetString() == publishRequest.LayerName);
            return;
        }
        var published = await PublishLayerAsync(publishRequest);
        _layerId = published.LayerId;
        published.GeometryType.Should().Be(canonicalType);

        using var featureMetadata = await GetGeometryJsonAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        if (esriType is null)
        {
            featureMetadata.RootElement.TryGetProperty("geometryType", out _).Should().BeFalse(
                "mixed geometry has no single FeatureServer geometry type");
        }
        else
        {
            featureMetadata.RootElement.GetProperty("geometryType").GetString().Should().Be(esriType);
        }
        using var ogcMetadata = await GetGeometryJsonAsync($"/ogc/features/collections/{_layerId}?f=json");
        ogcMetadata.RootElement.GetProperty("geometryType").GetString().Should().Be(canonicalType);
        ogcMetadata.RootElement.GetProperty("storageCrs").GetString().Should().Contain(targetSrid == 4326 ? "CRS84" : "3857");

        using var items = JsonDocument.Parse(await ReadOgcItemsAsync("sortby=population"));
        var ogcFeatures = items.RootElement.GetProperty("features");
        ogcFeatures.GetArrayLength().Should().Be(2);
        ogcFeatures[0].GetProperty("geometry").GetProperty("type").GetString().Should().Be(
            scenario == "Mixed" ? "Polygon" : scenario == "PointCsv" ? "Point" : scenario);
        ogcFeatures[1].GetProperty("geometry").GetProperty("type").GetString().Should().Be(
            scenario is "Mixed" or "PointCsv" ? "Point" : scenario);
        ogcFeatures[0].GetProperty("properties").GetProperty("population").GetInt64().Should().Be(2);
        using var features = JsonDocument.Parse(await ReadFeatureServerAsync("1=1", "population ASC"));
        var serverFeatures = features.RootElement.GetProperty("features");
        serverFeatures.GetArrayLength().Should().Be(2);
        serverFeatures[0].GetProperty("attributes").GetProperty("population").GetInt64().Should().Be(2);
        serverFeatures[0].GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Object);
        serverFeatures[1].GetProperty("geometry").ValueKind.Should().Be(JsonValueKind.Object);

        var catalog = await CallGeometryCatalogAsync("honua_list_layers", new { filter = _serviceName });
        var layer = catalog.GetProperty("layers").EnumerateArray().Single(layer => layer.GetProperty("layerId").GetInt32() == _layerId);
        layer.GetProperty("geometryType").GetString().Should().Be(canonicalType);
        layer.GetProperty("srid").GetInt32().Should().Be(targetSrid);
        var described = await CallGeometryCatalogAsync("honua_describe_layer", new { serviceId = _serviceName, layerId = _layerId });
        described.GetProperty("geometryType").GetString().Should().Be(canonicalType);
        described.GetProperty("srid").GetInt32().Should().Be(targetSrid);
    }

    private async Task<JsonDocument> GetGeometryJsonAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        return JsonDocument.Parse(payload);
    }

    private async Task<JsonElement> CallGeometryCatalogAsync(string name, object arguments)
    {
        using var response = await _client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = "import-geometry", method = "tools/call", @params = new { name, arguments }
        });
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        var result = document.RootElement.GetProperty("result");
        result.TryGetProperty("isError", out var error);
        error.ValueKind.Should().NotBe(JsonValueKind.True, payload);
        return result.GetProperty("structuredContent").Clone();
    }
}
