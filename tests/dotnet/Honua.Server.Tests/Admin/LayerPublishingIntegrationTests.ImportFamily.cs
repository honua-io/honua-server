// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Protocol(TestProtocols.OgcApiFeatures, TestProtocols.FeatureServer, TestProtocols.Mcp)]
    [Operation(Operations.Import, Operations.Create, Operations.Query, Operations.ApplyEdits, Operations.GetMetadata)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("GET /api/v1/admin/connections/{id}/tables")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    [Endpoint("GET /ogc/features/collections/{collectionId}")]
    [Endpoint("POST /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items/{featureId}")]
    [Endpoint("POST /mcp tools/call honua_describe_layer")]
    public async Task PublishImportedPolygonFamily_SingleAndMultipartEdits_StoreMultiPolygon(bool mixedParts)
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var publishConnection = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(publishConnection.ConnectionString);
        const string single = """{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}""";
        const string multi = """{"type":"MultiPolygon","coordinates":[[[[2,2],[3,2],[3,3],[2,2]]]]}""";
        var firstGeometry = mixedParts ? single : multi;
        var source = $$$"""
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{{{firstGeometry}}},"properties":{"name":"Alpha","population":2}},
              {"type":"Feature","geometry":{{{multi}}},"properties":{"name":"Bravo","population":10}}]}
            """;
        using var upload = new MultipartFormDataContent();
        upload.Add(new StringContent(source), "File", "polygon-family.geojson");
        upload.Add(new StringContent($"family_{Guid.NewGuid():N}"), "TableName");
        upload.Add(new StringContent("4326"), "TargetSrid");
        using var uploadResponse = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var uploadPayload = await uploadResponse.Content.ReadAsStringAsync();
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK, uploadPayload);
        var imported = JsonSerializer.Deserialize<ImportResult>(uploadPayload, _jsonOptions)!;
        imported.Success.Should().BeTrue(uploadPayload);
        imported.TypedColumns.Should().Contain(column => column.Name == "population" && column.PostgresType == "bigint");
        _importedTableSchema = imported.Schema;
        _importedTableName = imported.PhysicalTableName;
        using var discovery = await GetGeometryJsonAsync($"/api/v1/admin/connections/{_connectionId}/tables");
        var table = discovery.RootElement.GetProperty("tables").EnumerateArray().Single(item =>
            item.GetProperty("schema").GetString() == imported.Schema && item.GetProperty("table").GetString() == imported.PhysicalTableName);
        table.GetProperty("geometryType").GetString().Should().Be("MULTIPOLYGON");
        table.GetProperty("srid").GetInt32().Should().Be(4326);
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM \"{imported.Schema}\".\"{imported.PhysicalTableName}\" WHERE ST_GeometryType(geometry) = 'ST_MultiPolygon'";
            (await command.ExecuteScalarAsync()).Should().Be(2L);
        }
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = imported.Schema!,
            Table = imported.PhysicalTableName!,
            LayerName = "Polygon family",
            PrimaryKey = "id",
            Fields = ["id", "name", "population", "properties"],
            ServiceName = _serviceName,
            Enabled = true,
            CreateEditableCopy = true
        }, _connectionName);
        _layerId = published.LayerId;
        published.GeometryType.Should().Be("MultiPolygon");
        using var serverMetadata = await GetGeometryJsonAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}?f=json");
        serverMetadata.RootElement.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolygon");
        using var ogcMetadata = await GetGeometryJsonAsync($"/ogc/features/collections/{_layerId}?f=json");
        ogcMetadata.RootElement.GetProperty("geometryType").GetString().Should().Be("MultiPolygon");
        var described = await CallGeometryCatalogAsync("honua_describe_layer", new { serviceId = _serviceName, layerId = _layerId });
        described.GetProperty("geometryType").GetString().Should().Be("MultiPolygon");

        var collection = $"/ogc/features/collections/{_layerId}/items";
        foreach (var (geometry, name) in new[] { (single, "single"), (multi, "multi") })
        {
            var body = $$$"""{"type":"Feature","geometry":{{{geometry}}},"properties":{"name":"{{{name}}}","population":3}}""";
            using var content = new StringContent(body, Encoding.UTF8, "application/geo+json");
            using var created = await _client.PostAsync(collection, content);
            var payload = await created.Content.ReadAsStringAsync();
            created.StatusCode.Should().Be(HttpStatusCode.Created, payload);
            using var readback = await GetGeometryJsonAsync(created.Headers.Location!.ToString());
            readback.RootElement.GetProperty("geometry").GetProperty("type").GetString().Should().Be("MultiPolygon");
        }

        foreach (var rings in new[]
                 {
                     "[[[4,4],[4,5],[5,5],[4,4]]]",
                     "[[[4,4],[4,5],[5,5],[4,4]],[[7,7],[7,8],[8,8],[7,7]]]"
                 })
        {
            using var edit = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["adds"] = $$$"""[{"attributes":{"name":"FeatureServer edit","population":4},"geometry":{"rings":{{{rings}}}}}]"""
            });
            using var edited = await _client.PostAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}/applyEdits", edit);
            var editPayload = await edited.Content.ReadAsStringAsync();
            edited.StatusCode.Should().Be(HttpStatusCode.OK, editPayload);
            using var editResult = JsonDocument.Parse(editPayload);
            editResult.RootElement.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(editPayload);
            var newId = editResult.RootElement.GetProperty("addResults")[0].GetProperty("objectId").GetInt64();
            using var serverReadback = await GetGeometryJsonAsync($"{collection}/{newId}");
            serverReadback.RootElement.GetProperty("geometry").GetProperty("type").GetString().Should().Be("MultiPolygon");
            serverReadback.RootElement.GetProperty("properties").GetProperty("population").GetInt64().Should().Be(4);
        }
    }
}
