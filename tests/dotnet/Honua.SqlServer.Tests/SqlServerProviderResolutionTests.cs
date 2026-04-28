// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.SqlServer.Features.FeatureStore;
using Honua.SqlServer.Features.FeatureStore.Services;

namespace Honua.SqlServer.Tests;

/// <summary>
/// Verifies that the SQL Server provider plugs into the shared provider seam:
/// it is discoverable by canonical name and aliases, advertises read-only capabilities,
/// and resolves through <see cref="FeatureProviderBindingResolver"/> for layers whose
/// <see cref="DataConnection"/> selects the SQL Server engine.
/// </summary>
public class SqlServerProviderResolutionTests
{
    [Theory]
    [InlineData("sqlserver")]
    [InlineData("mssql")]
    [InlineData("SqlServer")]
    public void Registry_ResolvesProviderByCanonicalNameAndAliases(string providerName)
    {
        var sqlProvider = CreateSqlServerStore();
        var registry = new FeatureDataProviderRegistry([sqlProvider]);

        Assert.True(registry.TryGetProvider(providerName, out var resolved));
        Assert.Same(sqlProvider, resolved);
    }

    [Fact]
    public void Capabilities_AreReadOnlyWithStreamingDisabled()
    {
        var provider = CreateSqlServerStore();

        var caps = provider.Capabilities;

        Assert.Equal(DataProviderNames.SqlServer, provider.ProviderName);
        Assert.True(caps.SupportsQuery);
        Assert.True(caps.SupportsCount);
        Assert.True(caps.SupportsExtent);
        Assert.False(caps.SupportsStatistics);
        Assert.Equal(FeatureProviderEditCapabilities.ReadOnly, caps.Edits);
        Assert.False(caps.Outputs.SupportsStreamingGeoJson);
        Assert.False(caps.Outputs.SupportsNativeMvt);
        Assert.False(caps.Outputs.SupportsNativeFlatGeobuf);
        Assert.False(caps.Outputs.SupportsNativeGeobuf);
        Assert.False(caps.Outputs.SupportsNativeGml);
        Assert.Null(provider.Writer);
    }

    [Fact]
    public async Task BindingResolver_RoutesSqlServerConnectionToSqlServerProvider()
    {
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection { ConnectionId = connectionId, Provider = DataProviderNames.SqlServer };
        var connectionRegistry = new FakeSecureConnectionRegistry(connection);

        var sqlProvider = CreateSqlServerStore();
        var providerRegistry = new FeatureDataProviderRegistry([sqlProvider]);

        var resolver = new FeatureProviderBindingResolver(
            connectionRegistry,
            providerRegistry,
            DataProviderNames.SqlServer);

        var layer = LayerDefinition.CreateBasic(1, "Parcels", GeometryType.Polygon)
            with
        { StorageMapping = new LayerStorageMapping("parcels", SchemaName: "dbo", PrimaryKeyColumn: "objectid", GeometryColumn: "shape", StorageSrid: 4326) };

        var service = new ServiceDefinition(
            Name: "Parcels",
            Description: "test",
            Layers: [layer],
            SpatialReference: Honua.Core.Features.Shared.Models.SpatialReference.WGS84,
            Capabilities: ["Query"],
            ConnectionId: connectionId);

        var binding = await resolver.ResolveAsync(service, layer);

        Assert.Same(sqlProvider, binding.Provider);
        Assert.Equal(DataProviderNames.SqlServer, binding.Provider.ProviderName);
    }

    [Fact]
    public async Task QueryStatistics_OnSqlServerProvider_Throws()
    {
        var provider = CreateSqlServerStore();
        var reader = provider.Reader;
        await Assert.ThrowsAsync<NotSupportedException>(
            () => reader.QueryStatisticsAsync(1, new FeatureQuery()));
    }

    private static SqlServerFeatureStore CreateSqlServerStore()
    {
        var dataAccess = new SqlServerFeatureDataAccess(
            new ThrowingConnectionFactory(),
            Microsoft.Extensions.Options.Options.Create(new SqlServerOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SqlServerFeatureDataAccess>.Instance);

        return new SqlServerFeatureStore(dataAccess, new EmptyLayerCatalog());
    }

    private sealed class ThrowingConnectionFactory : ISqlServerConnectionFactory
    {
        public Task<Microsoft.Data.SqlClient.SqlConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Connection access not expected in resolution tests.");
    }

    private sealed class EmptyLayerCatalog : ILayerCatalog
    {
        public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult<LayerDefinition?>(null);

        public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<LayerDefinition>());

        public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult<ServiceDefinition?>(null);

        public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<ServiceDefinition>());

        public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
            => Task.FromResult<Relationship?>(null);

        public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
            => Task.FromResult(Array.Empty<Relationship>());
    }

    private sealed class FakeSecureConnectionRegistry(DataConnection connection) : ISecureConnectionRegistry
    {
        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult<DataConnection?>(connection.ConnectionId == connectionId ? connection : null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId)
            => Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
            => Task.FromResult<DataConnection?>(null);

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
