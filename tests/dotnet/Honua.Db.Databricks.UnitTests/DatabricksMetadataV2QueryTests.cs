// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Db.Databricks.Features.FeatureStore;
using Honua.Db.Databricks.Features.FeatureStore.Services;
using Honua.Db.Databricks.Features.Infrastructure;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Tests;

/// <summary>
/// Metadata v2 layer 0 is the storage handle FeatureServer queries. The provider must
/// serve that query from the storage binding with no <c>Databricks:Layers[]</c> entry.
/// </summary>
[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksMetadataV2QueryTests
{
    [Theory]
    [InlineData("databricks")]
    [InlineData("databrickssql")]
    [InlineData("dbsql")]
    public async Task Query_MetadataV2Connection_ServesFeatureServerLayer0WithoutLayersConfig(string providerAlias)
    {
        const string json = """
        {
          "statement_id": "q-layer0",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_id", "type_name": "LONG", "position": 0 },
            { "name": "__honua_geom_hex", "type_name": "STRING", "position": 1 },
            { "name": "name", "type_name": "STRING", "position": 2 }
          ] } },
          "result": { "data_array": [ ["7", "0101000000000000000000F03F0000000000000040", "parcel-7"] ] }
        }
        """;
        var handler = new BodyCapturingHandler();
        handler.EnqueueJson(json);
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = providerAlias,
            Name = "warehouse",
            Host = "example.cloud.databricks.com",
            DatabaseName = "wh-123",
            Username = "token"
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId: 0,
            FeatureProviderReadOperation.Query);

        var result = await reader.QueryAsync(0, new FeatureQuery { Limit = 10 });

        Assert.NotSame(store, reader);
        var feature = Assert.Single(result.Items);
        Assert.Equal(7, feature.Id);
        Assert.NotNull(feature.Geometry);
        Assert.Equal("parcel-7", feature.Attributes["name"]);
        var body = Assert.Single(handler.Bodies);
        using var document = JsonDocument.Parse(body);
        var statement = document.RootElement.GetProperty("statement").GetString();
        Assert.NotNull(statement);
        Assert.Contains("`main`.`gis`.`parcels`", statement!, StringComparison.Ordinal);
        Assert.DoesNotContain("static_only", statement!, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NonDatabricksConnection_FallsBackToConfiguredLayers()
    {
        var configured = new DatabricksLayerMapping
        {
            LayerId = 0,
            Table = "static_only",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            GeometryType = Honua.Core.Features.Catalog.Domain.GeometryType.Point,
            AttributeColumns = []
        };
        var registry = new DatabricksLayerMappingRegistry([configured]);
        var binding = CreateBinding(providerAlias: "postgis", connectionId: Guid.NewGuid());

        var resolved = registry.Resolve(0, binding);

        Assert.Equal("static_only", resolved.Table);
    }

    private static DatabricksFeatureStore CreateStore(BodyCapturingHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://example.cloud.databricks.com") };
        var options = Options.Create(new DatabricksOptions
        {
            Host = "https://example.cloud.databricks.com",
            WarehouseId = "wh-123",
            Token = "dapi-test",
            CommandTimeoutSeconds = 30,
            PollIntervalMilliseconds = 1,
            Layers = []
        });
        var client = new DatabricksStatementClient(httpClient, options);
        return new DatabricksFeatureStore(
            new DatabricksLayerMappingRegistry([]),
            new DatabricksFeatureQueryBuilder(),
            new DatabricksFeatureDataAccess(client));
    }

    private static FeatureProviderBinding CreateBinding(string providerAlias, Guid connectionId)
    {
        var store = new DatabricksFeatureStore(
            new DatabricksLayerMappingRegistry([]),
            new DatabricksFeatureQueryBuilder(),
            new DatabricksFeatureDataAccess(new DatabricksStatementClient(
                new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("https://example.cloud.databricks.com") },
                Options.Create(new DatabricksOptions
                {
                    Host = "https://example.cloud.databricks.com",
                    WarehouseId = "wh-123",
                    Token = "dapi-test"
                }))));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");
        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            0,
            store,
            new DataConnection
            {
                ConnectionId = connectionId,
                Provider = providerAlias,
                Name = "other"
            });
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateLayer0Snapshot(Guid connectionId)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-parcels", Name = "Parcels" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-parcels"],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = "id",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "geom",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "geom"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "main.gis.parcels",
            StorageLayerId = 0,
            Options = new Dictionary<string, JsonElement>
            {
                ["catalogName"] = JsonSerializer.SerializeToElement("main"),
                ["schemaName"] = JsonSerializer.SerializeToElement("gis"),
                ["tableName"] = JsonSerializer.SerializeToElement("parcels"),
                ["geometryColumn"] = JsonSerializer.SerializeToElement("geom"),
                ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("id"),
                ["storageSrid"] = JsonSerializer.SerializeToElement(4326)
            }
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "warehouse" },
            Provider = DataProviderNames.Databricks
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = "0", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Connections = [metadataConnection],
            StorageBindings = [storageBinding],
            Services = [service],
            Publications = [publication]
        };

        return (new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }

    private sealed class BodyCapturingHandler : StubHttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                Bodies.Add(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class FakeSecureConnectionRegistry(DataConnection connection) : ISecureConnectionRegistry
    {
        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(connection.ConnectionId == connectionId ? connection : null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId)
            => Guid.TryParse(connectionId, out var id)
                ? GetConnectionAsync(id)
                : Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
            => GetConnectionAsync(connectionId);

        public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection> CreateConnectionAsync(DataConnection conn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RegisterConnectionAsync(DataConnection conn) => throw new NotSupportedException();

        public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
            => Task.FromResult<IEnumerable<DataConnection>>([connection]);

        public Task<bool> RemoveConnectionAsync(string connectionId) => Task.FromResult(false);

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
            => Task.FromResult(new Dictionary<string, ConnectionHealthStatus>());

        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<DataConnection>>([connection]);

        public Task<DataConnection> UpdateConnectionAsync(DataConnection conn, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
