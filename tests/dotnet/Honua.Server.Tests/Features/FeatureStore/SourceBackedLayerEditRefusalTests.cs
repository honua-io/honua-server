// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using MetadataV2ServiceProtocols = Honua.Core.Features.Metadata.Domain.V2.ServiceProtocols;

namespace Honua.Server.Tests.Features.FeatureStore;

// A layer served from a source table must refuse edits even when its metadata declares them: the
// managed writer would store the feature where the layer never reads it back.
[Collection("Database")]
[Protocol(TestProtocols.FeatureServer)]
public sealed class SourceBackedLayerEditRefusalTests : IAsyncLifetime
{
    private const string Marker = "source-edit-refused";
    private const string SourceTable = "source_edit_refusal";
    private const string ServiceName = "sourceedits";
    private const int StorageLayerId = 6357;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/applyEdits")]
    public async Task ApplyEdits_LayerServedFromSourceTable_IsRefusedAndStoresNothing()
    {
        var schema = _fixture.CurrentSchema!;
        await ExecuteAsync(schema, $"""
            CREATE TABLE {SourceTable} (objectid bigint PRIMARY KEY, name text NOT NULL, geom geometry(Point, 4326) NOT NULL);
            INSERT INTO {SourceTable} VALUES (1, 'seed', ST_SetSRID(ST_MakePoint(10, 20), 4326));
            """);
        _fixture.Services.GetRequiredService<TestMetadataV2GraphProvider>()
            .SetGraph(BuildGraph(schema), schema: schema);
        var adds = JsonSerializer.Serialize(new[]
        {
            new { geometry = new { x = 10.5, y = 20.5, spatialReference = new { wkid = 4326 } }, attributes = new { name = Marker } }
        });

        using var client = _fixture.CreateAdminClient();
        using var response = await client.PostAsync(
            $"/rest/services/{ServiceName}/FeatureServer/1/applyEdits",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["adds"] = adds }));
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed, body);
        body.Should().NotContain("\"success\":true", body);
        body.Should().NotContain(SourceTable, "the refusal must not disclose the internal table name");
        (await CountAsync(schema, $"SELECT COUNT(*) FROM \"{schema}\".features WHERE attributes->>'name' = '{Marker}'"))
            .Should().Be(0, "a refused edit must not reach the managed features table");
        (await CountAsync(schema, $"SELECT COUNT(*) FROM {SourceTable}")).Should().Be(1);
    }

    private async Task ExecuteAsync(string schema, string sql)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string schema, string sql)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static MetadataV2Graph BuildGraph(string schema)
    {
        const string resourceId = "res-source-edits";
        const string bindingId = "binding-source-edits";
        const string serviceId = "svc-source-edits";
        var open = new AccessPolicy { AllowAnonymous = true, AllowAnonymousWrite = true };

        return new TestMetadataV2GraphBuilder()
            .AddResource(resourceId, "source_edits", MetadataV2ObjectType.FeatureDataset, accessPolicy: open,
                fields: Fields(), spatial: PointSpatial())
            .AddStorageBinding(
                bindingId,
                resourceId,
                SourceTable,
                storageLayerId: StorageLayerId,
                options: new Dictionary<string, JsonElement>
                {
                    ["schemaName"] = JsonSerializer.SerializeToElement(schema),
                    ["tableName"] = JsonSerializer.SerializeToElement(SourceTable),
                    ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("objectid"),
                    ["geometryColumn"] = JsonSerializer.SerializeToElement("geom"),
                    ["sourceBacked"] = JsonSerializer.SerializeToElement(true)
                })
            .AddService(
                serviceId,
                ServiceName,
                accessPolicy: open,
                protocols: [MetadataV2ServiceProtocols.FeatureServer],
                options: new Dictionary<string, JsonElement>
                {
                    ["capabilities"] = JsonSerializer.SerializeToElement(new[] { "Query", "Create", "Update", "Delete" })
                })
            .AddPublication("source_edits", serviceId, resourceId, layerIndex: 1, storageBindingId: bindingId,
                publicationType: MetadataV2LayerType.EsriFeatureLayer)
            .Build();
    }

    private static MetadataV2Field[] Fields() =>
    [
        new() { Name = "objectid", Type = MetadataV2FieldType.BigInteger, Nullable = false, SemanticRoles = ["id.primary"] },
        new() { Name = "name", Type = MetadataV2FieldType.String, Nullable = false },
        new() { Name = "geom", Type = MetadataV2FieldType.Geometry, Nullable = false, SemanticRoles = ["geometry.primary"] }
    ];

    private static MetadataV2ObjectSpatial PointSpatial() => new()
    {
        SpatialReference = MetadataV2SpatialReference.Wgs84,
        GeometryType = MetadataV2GeometryType.Point,
        PrimaryGeometryField = "geom",
        SupportedCrs = [MetadataV2SpatialReference.Wgs84]
    };
}
