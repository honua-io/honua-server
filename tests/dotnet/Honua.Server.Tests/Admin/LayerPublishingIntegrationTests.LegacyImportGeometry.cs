// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Admin;

public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTheory]
    [InlineData("Append")]
    [InlineData("Replace")]
    [Protocol(TestProtocols.FeatureServer)]
    [Operation(Operations.Import, Operations.ApplyEdits)]
    [Endpoint("POST /api/v1/admin/import/upload")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task LegacyImportedTable_PublishedBeforeLaterImport_PreservesSchemaAndEditableCopy(string mode)
    {
        await InstallImportFunctionsAsync();
        await DeleteSecureConnectionAsync();
        _connectionCreated = false;
        var publishConnection = new Npgsql.NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString)
        {
            SearchPath = $"{_fixture.CurrentSchema},public"
        };
        await CreateSecureConnectionAsync(publishConnection.ConnectionString);
        var tableName = $"legacy_{Guid.NewGuid():N}";
        const string source = "name,population,longitude,latitude\nAlpha,2,0.5,0.5\n";
        async Task<ImportResult> UploadAsync(string loadMode)
        {
            using var upload = new MultipartFormDataContent();
            upload.Add(new StringContent(source), "File", "points.csv");
            upload.Add(new StringContent(tableName), "TableName");
            upload.Add(new StringContent("4326"), "TargetSrid");
            upload.Add(new StringContent(loadMode), "LoadMode");
            upload.Add(new StringContent(loadMode == "Replace" ? "true" : "false"), "OverwriteExisting");
            using var response = await _client.PostAsync("/api/v1/admin/import/upload", upload);
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
            var imported = JsonSerializer.Deserialize<ImportResult>(payload, _jsonOptions)!;
            imported.Success.Should().BeTrue(payload);
            return imported;
        }
        var created = await UploadAsync("Replace");
        _importedTableSchema = created.Schema;
        _importedTableName = created.PhysicalTableName;
        var relation = $"\"{created.Schema}\".\"{created.PhysicalTableName}\"";
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            await using var legacy = connection.CreateCommand();
            legacy.CommandText = $"ALTER TABLE {relation} DROP CONSTRAINT honua_import_geometry_type; UPDATE {relation} SET geometry = ST_GeometryN(geometry, 1)";
            await legacy.ExecuteNonQueryAsync();
        }
        var sourceLayer = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = created.Schema!,
            Table = created.PhysicalTableName!,
            LayerName = "Legacy source",
            GeometryColumn = "geometry",
            GeometryType = "Point",
            PrimaryKey = "id",
            Fields = ["id", "name", "population", "properties"],
            ServiceName = _serviceName,
            Enabled = true
        }, _connectionName);
        var published = await PublishLayerAsync(new PublishLayerRequest
        {
            Schema = created.Schema!,
            Table = created.PhysicalTableName!,
            LayerName = "Legacy point",
            GeometryColumn = "geometry",
            GeometryType = "Point",
            PrimaryKey = "id",
            Fields = ["id", "name", "population", "properties"],
            ServiceName = _serviceName,
            Enabled = true,
            CreateEditableCopy = true
        }, _connectionName);
        _layerId = published.LayerId;
        await UploadAsync(mode);
        using (var sourceResponse = await GetGeometryJsonAsync(
            $"/rest/services/{_serviceName}/FeatureServer/{sourceLayer.LayerId}/query?f=json&where=1%3D1&outFields=*&returnGeometry=true"))
        {
            var sourceFeatures = sourceResponse.RootElement.GetProperty("features");
            sourceFeatures.GetArrayLength().Should().Be(mode == "Append" ? 2 : 1);
            sourceFeatures[0].GetProperty("geometry").GetProperty("x").GetDouble().Should().BeApproximately(0.5, 0.00001);
        }
        await using (var connection = await _fixture.Postgres.GetConnectionAsync())
        {
            await using var check = connection.CreateCommand();
            check.CommandText = $"SELECT count(*) FILTER (WHERE ST_GeometryType(geometry) <> 'ST_Point') FROM {relation}";
            (await check.ExecuteScalarAsync()).Should().Be(0L);
            check.CommandText = "SELECT count(*) FROM pg_constraint WHERE conrelid = @relation::regclass AND conname = 'honua_import_geometry_type'";
            check.Parameters.AddWithValue("relation", relation);
            (await check.ExecuteScalarAsync()).Should().Be(0L);
            // Source-backed layers are read-only; verify the live table still accepts
            // the single-part geometry that a legacy layer represents.
            check.Parameters.Clear();
            check.CommandText = $"INSERT INTO {relation} (geometry) VALUES (ST_SetSRID(ST_MakePoint(2, 3), 4326))";
            (await check.ExecuteNonQueryAsync()).Should().Be(1);
        }
        using var edit = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json",
            ["adds"] = """[{"attributes":{"name":"Edited","population":4},"geometry":{"x":2,"y":3}}]"""
        });
        using var edited = await _client.PostAsync($"/rest/services/{_serviceName}/FeatureServer/{_layerId}/applyEdits", edit);
        var payload = await edited.Content.ReadAsStringAsync();
        edited.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var result = JsonDocument.Parse(payload);
        result.RootElement.GetProperty("addResults")[0].GetProperty("success").GetBoolean().Should().BeTrue(payload);
    }
}
