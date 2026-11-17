// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Infrastructure.Models;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Admin;

// Publishing a managed layer with a global-ID field builds the identity index inside the publish
// transaction, instead of waiting on its own uncommitted rows until the command times out.
public sealed partial class LayerPublishingIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.Create)]
    [Protocol(TestProtocols.Admin)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_ManagedWithGlobalIdField_BuildsIdentityIndexInsideThePublish()
    {
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            ALTER TABLE public.{_tableName} ADD COLUMN gid uuid;
            UPDATE public.{_tableName} SET gid = '6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f';
            """);

        var (status, payload) = await PublishManagedGidLayerTimedAsync();

        status.Should().Be(HttpStatusCode.Created, $"response: {payload}");
        var api = JsonSerializer.Deserialize<ApiResponse<PublishedLayerSummary>>(payload, _jsonOptions);
        _layerId = api!.Data!.StorageLayerId;
        (await GetGlobalIdIndexDefinitionAsync(_layerId.Value)).Should().Contain("CREATE UNIQUE INDEX")
            .And.Contain("((NULLIF((attributes ->> 'gid'::text), ''::text))::uuid)")
            .And.Contain($"WHERE (layer_id = {_layerId.Value})");
        (await GetManagedStoreRowCountAsync(_layerId.Value)).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Protocol(TestProtocols.Admin)]
    [Endpoint("POST /api/v1/admin/connections/{id}/layers")]
    public async Task PublishLayer_ManagedWithGlobalIdFieldOverDuplicates_RefusesWithValidationError()
    {
        await AddDuplicateGlobalIdsAsync();

        var (status, payload) = await PublishManagedGidLayerTimedAsync();

        status.Should().Be(HttpStatusCode.BadRequest, $"response: {payload}");
        payload.Should().Contain("GlobalIdField 'gid'");
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Protocol(TestProtocols.Admin)]
    [Endpoint("PUT /api/v1/admin/services/{serviceName}/layers/{serviceLayerIndex}/metadata")]
    public async Task UpdateLayerMetadata_GlobalIdOverDuplicateValues_RefusesWithConflict()
    {
        await AddDuplicateGlobalIdsAsync();
        await UseServerFeatureStoreConnectionAsync();
        var published = await PublishLayerAsync(ManagedGidPublishRequest(globalIdField: null));
        _layerId = published.StorageLayerId;

        var response = await _client.PutAsync(
            $"/api/v1/admin/services/{_serviceName}/layers/{_layerId.Value}/metadata",
            JsonContent.Create(new { editing = new { globalIdField = "gid" } }));

        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, $"response: {payload}");
        payload.Should().Contain("Repair those values");
        (await GetGlobalIdIndexDefinitionAsync(_layerId.Value)).Should().BeEmpty();
        (await GetManagedStoreRowCountAsync(_layerId.Value)).Should().Be(2);
    }

    private async Task<(HttpStatusCode StatusCode, string Payload)> PublishManagedGidLayerTimedAsync()
    {
        await UseServerFeatureStoreConnectionAsync();
        var started = DateTimeOffset.UtcNow;
        var result = await PostPublishLayerAsync(ManagedGidPublishRequest(globalIdField: "gid"));
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(20), "the publish must not wait on its own rows");
        return result;
    }

    private PublishLayerRequest ManagedGidPublishRequest(string? globalIdField) => new()
    {
        Schema = _schema,
        Table = _tableName,
        LayerName = $"Layer {_tableName}",
        GeometryColumn = "geom",
        GeometryType = "Point",
        Srid = 4326,
        PrimaryKey = "id",
        Fields = ["id", "name", "gid"],
        ServiceName = _serviceName,
        Enabled = true,
        StorageMode = "managed",
        GlobalIdField = globalIdField,
        Capabilities = ["Query", "Create", "Update", "Delete"]
    };

    private async Task AddDuplicateGlobalIdsAsync()
    {
        await InsertPostGisFeatureAsync("Same identity", 200, 2, 2);
        await _fixture.Postgres.ExecuteDdlUnderLockAsync($"""
            ALTER TABLE public.{_tableName} ADD COLUMN gid uuid;
            UPDATE public.{_tableName} SET gid = CASE WHEN name = 'Same identity'
                THEN '6F1C2D3E-4B5A-4C6D-8E7F-0A1B2C3D4E5F'::uuid ELSE '6f1c2d3e-4b5a-4c6d-8e7f-0a1b2c3d4e5f'::uuid END;
            """);
    }

    private async Task<string> GetGlobalIdIndexDefinitionAsync(int layerId)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(string_agg(indexdef, ' | '), '') FROM pg_indexes
            WHERE schemaname = @schema AND indexname = 'idx_features_gid_' || md5(@layer::text || ':gid')
            """;
        command.Parameters.AddWithValue("schema", _fixture.CurrentSchema!);
        command.Parameters.AddWithValue("layer", layerId);
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
