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
using Honua.TestKit.Attributes;
using Honua.Tests.ProviderUnitSupport;
using Microsoft.Extensions.Options;

namespace Honua.Db.Databricks.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public class DatabricksMetadataV2QueryTests
{
    private static readonly ParcelsLayerFixture Layer0 = new(DataProviderNames.Databricks, "main.gis.parcels")
    {
        LayerId = 0,
        ObjectIdField = "id",
        GeometryField = "geom",
        GeometryType = MetadataV2GeometryType.Point,
        AttributeFields = [new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String, Nullable = true }],
        ConnectionName = "warehouse",
        StorageOptions = new Dictionary<string, JsonElement>
        {
            ["catalogName"] = JsonSerializer.SerializeToElement("main"),
            ["schemaName"] = JsonSerializer.SerializeToElement("gis"),
            ["tableName"] = JsonSerializer.SerializeToElement("parcels"),
            ["geometryColumn"] = JsonSerializer.SerializeToElement("geom"),
            ["primaryKeyColumn"] = JsonSerializer.SerializeToElement("id"),
            ["storageSrid"] = JsonSerializer.SerializeToElement(4326)
        }
    };

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
        using var handler = new BodyCapturingHandler();
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

    [Fact]
    public async Task Query_BoundDataConnectionWithDistinctCredentials_RoutesToBoundWorkspaceAndToken()
    {
        const string json = """
        {
          "statement_id": "q-bound",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_id", "type_name": "LONG", "position": 0 },
            { "name": "__honua_geom_hex", "type_name": "STRING", "position": 1 },
            { "name": "name", "type_name": "STRING", "position": 2 }
          ] } },
          "result": { "data_array": [ ["11", "0101000000000000000000F03F0000000000000040", "tenant-parcel"] ] }
        }
        """;
        using var handler = new BodyCapturingHandler();
        handler.EnqueueJson(json);
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
            Name = "tenant-b",
            ConnectionString = "Host=tenant-b.cloud.databricks.com;WarehouseId=wh-tenant-b;Token=dapi-tenant-b",
            IsEncrypted = false
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        var result = await reader.QueryAsync(0, new FeatureQuery { Limit = 10 });

        Assert.Single(result.Items);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("tenant-b.cloud.databricks.com", request.RequestUri!.Host);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("dapi-tenant-b", request.Headers.Authorization?.Parameter);
        using var document = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("wh-tenant-b", document.RootElement.GetProperty("warehouse_id").GetString());
    }

    [Fact]
    public async Task Query_BoundDataConnectionForDifferentWorkspaceWithoutCredentials_FailsClosed()
    {
        using var handler = new BodyCapturingHandler();
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
            Name = "other-tenant",
            Host = "other-tenant.cloud.databricks.com",
            DatabaseName = "wh-other",
            IsEncrypted = true
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.QueryAsync(0, new FeatureQuery { Limit = 10 }));
        Assert.Empty(handler.Requests);
    }

    [UnitTest]
    public async Task Query_BoundDataConnectionWithSecretRefMatchingDefaultWorkspace_FailsClosed()
    {
        using var handler = new BodyCapturingHandler();
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
            Name = "secret-backed",
            Host = "example.cloud.databricks.com",
            DatabaseName = "wh-123",
            SecretRef = "aws-secrets://prod/databricks-token",
            IsEncrypted = true
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.QueryAsync(0, new FeatureQuery { Limit = 10 }));
        Assert.Empty(handler.Requests);
    }

    [UnitTest]
    public async Task Query_BoundDataConnectionWithEncryptedBytesMatchingDefaultWorkspace_FailsClosed()
    {
        using var handler = new BodyCapturingHandler();
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
            Name = "encrypted-tenant",
            Host = "example.cloud.databricks.com",
            DatabaseName = "wh-123",
            EncryptedConnectionString = [1, 2, 3, 4],
            IsEncrypted = true
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.QueryAsync(0, new FeatureQuery { Limit = 10 }));
        Assert.Empty(handler.Requests);
    }

    [UnitTest]
    public async Task Query_BoundDataConnectionWithEmptyHostAndWarehouse_FailsClosed()
    {
        using var handler = new BodyCapturingHandler();
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
            Name = "empty-target",
            Host = string.Empty,
            DatabaseName = string.Empty,
            IsEncrypted = true
        };
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([store]));
        var (snapshot, service, resource, publication) = CreateLayer0Snapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.QueryAsync(0, new FeatureQuery { Limit = 10 }));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Query_PastEndEmptyPageWithOffset_IssuesCountQueryInsteadOfFabricatingOffsetAsTotal()
    {
        const string emptyPageJson = """
        {
          "statement_id": "q-empty",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "__honua_id", "type_name": "LONG", "position": 0 },
            { "name": "__honua_geom_hex", "type_name": "STRING", "position": 1 },
            { "name": "name", "type_name": "STRING", "position": 2 }
          ] } },
          "result": { "data_array": [] }
        }
        """;
        const string countJson = """
        {
          "statement_id": "q-count",
          "status": { "state": "SUCCEEDED" },
          "manifest": { "schema": { "columns": [
            { "name": "count", "type_name": "LONG", "position": 0 }
          ] } },
          "result": { "data_array": [ ["5"] ] }
        }
        """;
        using var handler = new BodyCapturingHandler();
        handler.EnqueueJson(emptyPageJson);
        handler.EnqueueJson(countJson);
        var store = CreateStore(handler);
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "databricks",
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
            snapshot, service, resource, publication, storageLayerId: 0, FeatureProviderReadOperation.Query);

        var result = await reader.QueryAsync(0, new FeatureQuery { Offset = 50, Limit = 10 });

        Assert.Empty(result.Items);
        Assert.Equal(5, result.TotalCount);
        Assert.False(result.HasMoreResults);
        Assert.Equal(2, handler.Bodies.Count);
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
            new DatabricksFeatureDataAccess(client),
            new LayerReadSecurityResolver(null, null, null, null));
    }

    private static FeatureProviderBinding CreateBinding(string providerAlias, Guid connectionId)
    {
        var store = new DatabricksFeatureStore(
            new DatabricksLayerMappingRegistry([]),
            new DatabricksFeatureDataAccess(new DatabricksStatementClient(
                new HttpClient(new StubHttpMessageHandler()) { BaseAddress = new Uri("https://example.cloud.databricks.com") },
                Options.Create(new DatabricksOptions
                {
                    Host = "https://example.cloud.databricks.com",
                    WarehouseId = "wh-123",
                    Token = "dapi-test"
                }))),
            new LayerReadSecurityResolver(null, null, null, null));
        return Layer0.CreateBinding(
            store,
            new DataConnection
            {
                ConnectionId = connectionId,
                Provider = providerAlias,
                Name = "other"
            });
    }

    private static ParcelsLayerSnapshot CreateLayer0Snapshot(Guid connectionId) => Layer0.CreateSnapshot(connectionId);

    private sealed class BodyCapturingHandler : StubHttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        public new List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
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
