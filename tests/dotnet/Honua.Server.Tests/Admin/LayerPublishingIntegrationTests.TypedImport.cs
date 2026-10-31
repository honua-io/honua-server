// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Protocol(TestProtocols.OgcApiFeatures, TestProtocols.FeatureServer, TestProtocols.Mcp, TestProtocols.ODataV4)]
    [Operation(Operations.Import)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [Endpoint("GET /odata/Features({layerId})?$filter=...&$orderby=...")]
    public async Task PublishImportedGeoJson_TypedColumns_FilterAndSortAcrossFeatureSurfaces()
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var publishConnection = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(publishConnection.ConnectionString);

        var logicalTableName = $"typed_points_{Guid.NewGuid():N}";
        const string geoJson = """
            {
              "type": "FeatureCollection",
              "features": [
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [-122.4, 37.8] },
                  "properties": { "id": "100", "source_id": "001", "name": "Alpha", "population": 2, "is_open": false, "score": 1.5, "opened": "2020-01-15", "notes": { "kind": "park" } }
                },
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [-122.5, 37.9] },
                  "properties": { "id": "200", "source_id": "002", "name": "Bravo", "population": 10, "is_open": true, "score": 2.5, "opened": "2021-06-01", "notes": { "kind": "dock" } }
                }
              ]
            }
            """;

        using var upload = new MultipartFormDataContent();
        var file = new StringContent(geoJson, Encoding.UTF8, "application/geo+json");
        file.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "typed-points.geojson"
        };
        upload.Add(file);
        upload.Add(new StringContent(logicalTableName), "TableName");
        upload.Add(new StringContent("4326"), "TargetSrid");

        var uploadResponse = await _client.PostAsync("/api/v1/admin/import/upload", upload);
        var uploadPayload = await uploadResponse.Content.ReadAsStringAsync();
        uploadResponse.StatusCode.Should().Be(HttpStatusCode.OK, uploadPayload);
        var importResult = JsonSerializer.Deserialize<ImportResult>(uploadPayload, _jsonOptions);
        importResult.Should().NotBeNull();
        importResult!.Success.Should().BeTrue(uploadPayload);
        importResult.Schema.Should().NotBeNullOrWhiteSpace();
        importResult.PhysicalTableName.Should().NotBeNullOrWhiteSpace();
        importResult.TypedColumns.Select(column => (column.Name, column.PostgresType)).Should().BeEquivalentTo(
        [
            ("source_id", "text"),
            ("name", "text"),
            ("population", "bigint"),
            ("is_open", "boolean"),
            ("score", "double precision"),
            ("opened", "date")
        ]);
        _importedTableSchema = importResult.Schema;
        _importedTableName = importResult.PhysicalTableName;

        var columns = await ColumnNamesAsync(importResult.Schema!, importResult.PhysicalTableName!);
        columns.Should().Contain(["id", "name", "population", "properties", "geometry"]);
        columns.Should().NotContain("notes");

        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = importResult.Schema,
            Table = importResult.PhysicalTableName,
            LayerName = $"Typed import {logicalTableName}",
            GeometryColumn = "geometry",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = ["id", "source_id", "name", "population", "is_open", "score", "opened", "properties"],
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.LayerId;

        var sortedPayload = await ReadOgcItemsAsync("sortby=-population");
        using var sorted = JsonDocument.Parse(sortedPayload);
        var sortedFeatures = sorted.RootElement.GetProperty("features");
        sortedFeatures.GetArrayLength().Should().Be(2, sortedPayload);
        AssertTypedFeature(sortedFeatures[0], "Bravo", 10, "dock");
        AssertTypedFeature(sortedFeatures[1], "Alpha", 2, "park");

        var filteredPayload = await ReadOgcItemsAsync($"filter={Uri.EscapeDataString("population = 2")}");
        using var filtered = JsonDocument.Parse(filteredPayload);
        var filteredFeatures = filtered.RootElement.GetProperty("features");
        filteredFeatures.GetArrayLength().Should().Be(1, filteredPayload);
        AssertTypedFeature(filteredFeatures[0], "Alpha", 2, "park");

        var featureServerPayload = await ReadFeatureServerAsync("1=1", "population DESC");
        using var featureServer = JsonDocument.Parse(featureServerPayload);
        var featureServerFeatures = featureServer.RootElement.GetProperty("features");
        featureServerFeatures.GetArrayLength().Should().Be(2, featureServerPayload);
        AssertFeatureServerAttributes(featureServerFeatures[0].GetProperty("attributes"), "Bravo", 10);
        AssertFeatureServerAttributes(featureServerFeatures[1].GetProperty("attributes"), "Alpha", 2);

        var filteredServerPayload = await ReadFeatureServerAsync("population = 2", "population DESC");
        using var filteredServer = JsonDocument.Parse(filteredServerPayload);
        var filteredServerFeatures = filteredServer.RootElement.GetProperty("features");
        filteredServerFeatures.GetArrayLength().Should().Be(1, filteredServerPayload);
        AssertFeatureServerAttributes(filteredServerFeatures[0].GetProperty("attributes"), "Alpha", 2);

        foreach (var (field, expression) in new[]
        {
            ("source_id", "source_id = '002'"),
            ("name", "name = 'Bravo'"),
            ("is_open", "is_open = true"),
            ("population", "population = 10"),
            ("score", "score = 2.5"),
            ("opened", "opened = '2021-06-01'")
        })
        {
            var ogcSortPayload = await ReadOgcItemsAsync($"sortby=-{field}");
            using var ogcSort = JsonDocument.Parse(ogcSortPayload);
            ogcSort.RootElement.GetProperty("features")[0].GetProperty("properties")
                .GetProperty("name").GetString().Should().Be("Bravo", field);
            var ogcFilterPayload = await ReadOgcItemsAsync($"filter={Uri.EscapeDataString(expression)}");
            using var ogcFilter = JsonDocument.Parse(ogcFilterPayload);
            ogcFilter.RootElement.GetProperty("features").GetArrayLength().Should().Be(1, field);

            var serverPayload = await ReadFeatureServerAsync(expression, $"{field} DESC");
            using var serverResult = JsonDocument.Parse(serverPayload);
            serverResult.RootElement.GetProperty("features").GetArrayLength().Should().Be(1, field);
        }

        var odataResponse = await _client.GetAsync($"/odata/Features({_layerId})?$filter=population%20eq%202&$orderby=population%20desc");
        var odataPayload = await odataResponse.Content.ReadAsStringAsync();
        odataResponse.StatusCode.Should().Be(HttpStatusCode.OK, odataPayload);
        using var odata = JsonDocument.Parse(odataPayload);
        odata.RootElement.GetProperty("value").GetArrayLength().Should().Be(1, odataPayload);

        var mcp = await CallQueryFeaturesAsync("population = 2");
        if (mcp.TryGetProperty("isError", out var isError))
        {
            (isError.ValueKind == JsonValueKind.True).Should().BeFalse(mcp.GetRawText());
        }
        var mcpFeatures = mcp.GetProperty("structuredContent").GetProperty("features");
        mcpFeatures.GetArrayLength().Should().Be(1, mcp.GetRawText());
        AssertFeatureServerAttributes(mcpFeatures[0].GetProperty("attributes"), "Alpha", 2);
    }

    [IntegrationTest]
    [Protocol(TestProtocols.Admin, TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Create)]
    [Operation(Operations.Query)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    [Endpoint("GET /ogc/features/collections/{collectionId}/items")]
    public async Task PublishExistingJsonPropertiesTable_StillServesTheDocument()
    {
        await InstallImportFunctionsAsync();
        var tableName = $"legacy_json_{Guid.NewGuid():N}";
        _importedTableSchema = "public";
        _legacyImportedTableName = tableName;

        await _fixture.Postgres.ExecuteDdlUnderLockAsync($$"""
            SELECT honua.create_import_table('public', '{{tableName}}', 4326);
            INSERT INTO public.{{tableName}} (geometry, properties)
            VALUES (ST_SetSRID(ST_MakePoint(-122.4, 37.8), 4326), '{"name":"Legacy","population":2}'::jsonb);
            """);

        var columns = await ColumnNamesAsync("public", tableName);
        columns.Should().BeEquivalentTo(["id", "geometry", "properties", "created_at"]);

        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = "public",
            Table = tableName,
            LayerName = $"Legacy jsonb {tableName}",
            GeometryColumn = "geometry",
            GeometryType = "Point",
            Srid = 4326,
            PrimaryKey = "id",
            Fields = ["id", "properties"],
            ServiceName = _serviceName,
            Enabled = true
        });
        _layerId = published.LayerId;

        var payload = await ReadOgcItemsAsync("limit=10");
        using var document = JsonDocument.Parse(payload);
        var features = document.RootElement.GetProperty("features");
        features.GetArrayLength().Should().Be(1, payload);
        var properties = features[0].GetProperty("properties");
        properties.TryGetProperty("name", out _).Should().BeFalse(payload);
        properties.TryGetProperty("created_at", out _).Should().BeFalse(payload);
        properties.GetProperty("properties").GetProperty("name").GetString().Should().Be("Legacy");
        (await ColumnNamesAsync("public", tableName)).Should().NotContain("name");
    }

    private async Task<string> ReadOgcItemsAsync(string query)
    {
        var response = await _client.GetAsync($"/ogc/features/collections/{_layerId}/items?f=json&{query}");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        return payload;
    }

    private async Task<string> ReadFeatureServerAsync(string where, string orderBy)
    {
        var response = await _client.GetAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{_layerId}/query?f=json"
            + $"&where={Uri.EscapeDataString(where)}"
            + $"&orderByFields={Uri.EscapeDataString(orderBy)}"
            + "&outFields=*&returnGeometry=true");
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        return payload;
    }

    private async Task<JsonElement> CallQueryFeaturesAsync(string where)
    {
        var body = new
        {
            jsonrpc = "2.0",
            id = "typed-import",
            method = "tools/call",
            @params = new
            {
                name = "honua_query_features",
                arguments = new
                {
                    serviceId = _serviceName,
                    layerId = _layerId,
                    where,
                    outFields = new[] { "name", "population" },
                    limit = 10
                }
            }
        };
        var response = await _client.PostAsJsonAsync("/mcp", body);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out var protocolError).Should().BeFalse(
            protocolError.ValueKind == JsonValueKind.Undefined ? payload : protocolError.GetRawText());
        return document.RootElement.GetProperty("result").Clone();
    }

    private async Task<List<string>> ColumnNamesAsync(string schema, string table)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name
            FROM information_schema.columns
            WHERE table_schema = @schema_name AND table_name = @table_name
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue("schema_name", schema);
        command.Parameters.AddWithValue("table_name", table);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private async Task InstallImportFunctionsAsync()
    {
        // Shared test databases can be left with stubbed import functions. Reinstall the
        // production chain, including the helpers 129 and 143 call.
        foreach (var name in new[]
        {
            "004_CreateImportFunctions.sql",
            "070_AddImportLoadModes.sql",
            "071_FixImportStagingTableNameLength.sql",
            "108_FixLongImportIndexNames.sql",
            "129_ProtectImportTableOwnership.sql",
            "143_PromoteImportTypedColumns.sql",
        })
        {
            await _fixture.Postgres.ApplyGlobalSeedSqlAsync(ReadMigration(name));
        }
    }

    private static void AssertTypedFeature(JsonElement feature, string name, long population, string noteKind)
    {
        feature.TryGetProperty("properties", out var properties).Should().BeTrue(feature.GetRawText());
        properties.GetProperty("name").GetString().Should().Be(name);
        properties.GetProperty("population").ValueKind.Should().Be(JsonValueKind.Number);
        properties.GetProperty("population").GetInt64().Should().Be(population);
        properties.TryGetProperty("created_at", out _).Should().BeFalse();
        properties.GetProperty("properties").GetProperty("notes").GetProperty("kind").GetString().Should().Be(noteKind);
        properties.GetProperty("properties").GetProperty("id").GetString().Should().Be(population == 2 ? "100" : "200");
    }

    private static void AssertFeatureServerAttributes(JsonElement attributes, string name, long population)
    {
        attributes.GetProperty("name").GetString().Should().Be(name);
        attributes.GetProperty("population").ValueKind.Should().Be(JsonValueKind.Number);
        attributes.GetProperty("population").GetInt64().Should().Be(population);
        attributes.TryGetProperty("created_at", out _).Should().BeFalse();
    }

    private static string ReadMigration(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Join(directory.FullName, "src", "Honua.Server", "Migrations", name);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Migration {name} was not found from the test output directory.");
    }
}
