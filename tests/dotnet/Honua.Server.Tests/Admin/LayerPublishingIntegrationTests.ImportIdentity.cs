// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Npgsql;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer, TestProtocols.Mcp)]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    public async Task PublishImportedSourceObjectId_RestAndMcpPreserveBusinessIdentity()
    {
        const string source = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-157.8,21.3]},"properties":{"objectid":1899,"name":"Alpha"}},
              {"type":"Feature","geometry":{"type":"Point","coordinates":[-155.1,19.7]},"properties":{"objectid":2848,"name":"Bravo"}}
            ]}
            """;
        await ImportAndPublishAttributesAsync(source, ["id", "objectid", "name", "properties"]);
        using var rest = JsonDocument.Parse(await ReadFeatureServerAsync("objectid=1899", "id ASC"));
        var feature = rest.RootElement.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject;
        feature.GetProperty("attributes").GetProperty("objectid").GetInt64().Should().Be(1899);
        feature.GetProperty("attributes").GetProperty("id").GetInt64().Should().Be(1);
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().BeApproximately(-157.8, 0.000001);
        using var canonical = JsonDocument.Parse(await ReadFeatureServerAsync("id=1", "id ASC"));
        canonical.RootElement.GetProperty("features")[0].GetProperty("attributes").GetProperty("objectid").GetInt64().Should().Be(1899);
        var catalogLayer = await ReadImportedGeometryCatalogLayerAsync();
        var mcp = await CallGeometryCatalogAsync("honua_query_features", new
        {
            serviceId = catalogLayer.GetProperty("serviceId").GetString(),
            serviceLayerIndex = catalogLayer.GetProperty("serviceLayerIndex").GetInt32(),
            where = "objectid=1899",
            outFields = new[] { "id", "objectid", "name" },
            limit = 10
        });
        var attributes = mcp.GetProperty("features").EnumerateArray().Should().ContainSingle().Subject.GetProperty("attributes");
        attributes.GetProperty("objectid").GetInt64().Should().Be(1899);
        attributes.GetProperty("name").GetString().Should().Be("Alpha");
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer, TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishImportedCsv_DefaultFields_ExposesSourceIdWithoutInternalColumns()
    {
        const string source = "id,name,lon,lat\n100,a,-157,21\n200,b,-156,21";
        await ImportAndPublishAttributesAsync(source, [], "attributes.csv");
        using var rest = JsonDocument.Parse(await ReadFeatureServerAsync("source_id=100", "id ASC"));
        var attributes = rest.RootElement.GetProperty("features").EnumerateArray()
            .Should().ContainSingle().Subject.GetProperty("attributes");
        attributes.GetProperty("source_id").GetInt64().Should().Be(100);
        attributes.GetProperty("name").GetString().Should().Be("a");
        attributes.TryGetProperty("properties", out _).Should().BeFalse();
        attributes.TryGetProperty("created_at", out _).Should().BeFalse();

        using var ogc = JsonDocument.Parse(await ReadOgcItemsAsync(
            $"filter={Uri.EscapeDataString("source_id = 100")}"));
        var properties = ogc.RootElement.GetProperty("features").EnumerateArray()
            .Should().ContainSingle().Subject.GetProperty("properties");
        properties.GetProperty("source_id").GetInt64().Should().Be(100);
        properties.GetProperty("name").GetString().Should().Be("a");
        properties.TryGetProperty("properties", out _).Should().BeFalse();
        properties.TryGetProperty("created_at", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task PublishOrdinaryTable_DefaultFields_PreservesUserPropertiesAndCreatedAt()
    {
        var tableName = $"user_attributes_{Guid.NewGuid():N}";
        _legacyImportedTableName = tableName;
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($$"""
            CREATE TABLE public.{{tableName}} (
                id integer PRIMARY KEY,
                geometry geometry(Point,4326),
                properties jsonb,
                created_at timestamptz);
            INSERT INTO public.{{tableName}} VALUES (
                1, ST_SetSRID(ST_MakePoint(-157,21),4326),
                '{"business":"value"}'::jsonb, '2026-01-01T00:00:00Z');
            """);
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = "public",
            Table = tableName,
            LayerName = "User attributes",
            PrimaryKey = "id",
            Fields = [],
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.StorageLayerId;
        using var rest = JsonDocument.Parse(await ReadFeatureServerAsync("id=1", "id ASC"));
        var attributes = rest.RootElement.GetProperty("features").EnumerateArray()
            .Should().ContainSingle().Subject.GetProperty("attributes");
        attributes.GetProperty("properties").GetProperty("business").GetString().Should().Be("value");
        attributes.TryGetProperty("created_at", out _).Should().BeTrue();
    }

    private async Task ImportAndPublishAttributesAsync(string source, string[] fields, string fileName = "attributes.geojson")
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var connection = new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(connection.ConnectionString);
        using var upload = new MultipartFormDataContent();
        using var fileContent = new StringContent(source);
        using var tableNameContent = new StringContent($"attributes_{Guid.NewGuid():N}");
        using var targetSridContent = new StringContent("4326");
        upload.Add(fileContent, "File", fileName);
        upload.Add(tableNameContent, "TableName");
        upload.Add(targetSridContent, "TargetSrid");
        using var response = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        var imported = JsonSerializer.Deserialize<ImportResult>(payload, _jsonOptions)!;
        imported.Success.Should().BeTrue(payload);
        imported.FeatureCount.Should().Be(2);
        _importedTableSchema = imported.Schema;
        _importedTableName = imported.PhysicalTableName;
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = imported.Schema!,
            Table = imported.PhysicalTableName!,
            LayerName = "Imported attributes",
            PrimaryKey = "id",
            Fields = fields,
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.StorageLayerId;
    }
}
