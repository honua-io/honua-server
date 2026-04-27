// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class FeatureProviderBindingResolverTests
{
    [Fact]
    public async Task ResolveAsync_ServiceConnectionAndLayerStorage_ReturnsMatchingProviderBinding()
    {
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Name = "analytics",
            Host = "db.example.com",
            Port = 5432,
            DatabaseName = "gis",
            Username = "app",
            Provider = "postgresql",
            SecretRef = "env:HONUA_TEST",
            SecretType = "environment",
            CreatedBy = "test"
        };

        var provider = new StubFeatureDataProvider(DataProviderNames.Postgis);
        var resolver = new FeatureProviderBindingResolver(
            new StubSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([provider]));

        var layer = CreateLayer();
        var service = new ServiceDefinition(
            "maps",
            "Maps",
            [layer],
            SpatialReference.WGS84,
            ConnectionId: connectionId);

        var binding = await resolver.ResolveAsync(service, layer);

        binding.Connection.Should().BeSameAs(connection);
        binding.Provider.Should().BeSameAs(provider);
        binding.StorageMapping.QualifiedName.Should().Be("public.roads");
        binding.StorageMapping.PrimaryKeyColumn.Should().Be("road_id");
    }

    [Fact]
    public async Task ResolveAsync_MissingStorageMapping_ThrowsClearError()
    {
        var provider = new StubFeatureDataProvider(DataProviderNames.Postgis);
        var resolver = new FeatureProviderBindingResolver(
            new StubSecureConnectionRegistry(),
            new FeatureDataProviderRegistry([provider]));
        var layer = CreateLayer() with { StorageMapping = null };
        var service = new ServiceDefinition("maps", "Maps", [layer], SpatialReference.WGS84);

        var action = () => resolver.ResolveAsync(service, layer);

        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("does not define a runtime storage mapping");
    }

    [Fact]
    public async Task ResolveAsync_UnregisteredProvider_ThrowsClearError()
    {
        var connectionId = Guid.NewGuid();
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Name = "warehouse",
            Host = "sql.example.com",
            Port = 1433,
            DatabaseName = "gis",
            Username = "app",
            Provider = DataProviderNames.SqlServer,
            SecretRef = "env:HONUA_SQLSERVER",
            SecretType = "environment",
            CreatedBy = "test"
        };

        var resolver = new FeatureProviderBindingResolver(
            new StubSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([new StubFeatureDataProvider(DataProviderNames.Postgis)]));
        var layer = CreateLayer();
        var service = new ServiceDefinition(
            "maps",
            "Maps",
            [layer],
            SpatialReference.WGS84,
            ConnectionId: connectionId);

        var action = () => resolver.ResolveAsync(service, layer);

        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("Feature provider 'sqlserver' is not registered");
    }

    private static LayerDefinition CreateLayer()
    {
        FieldDefinition[] fields =
        [
            new("road_id", FieldType.Integer, Nullable: false),
            new("geom", FieldType.Geometry, Nullable: false)
        ];

        return new LayerDefinition(
            1,
            "roads",
            "Road centerlines",
            GeometryType.LineString,
            SpatialReference.WGS84,
            fields,
            StorageMapping: new LayerStorageMapping(
                "roads",
                SchemaName: "public",
                PrimaryKeyColumn: "road_id",
                GeometryColumn: "geom",
                StorageSrid: 4326));
    }

    private sealed class StubFeatureDataProvider(string providerName) : IFeatureDataProvider
    {
        public string ProviderName { get; } = providerName;

        public FeatureProviderCapabilities Capabilities { get; } = FeatureProviderCapabilities.ReadOnlyAnalytical;

        public IFeatureReader Reader => throw new NotSupportedException();

        public IFeatureWriter? Writer => null;
    }

    private sealed class StubSecureConnectionRegistry(params DataConnection[] connections) : ISecureConnectionRegistry
    {
        private readonly Dictionary<Guid, DataConnection> _connections = connections.ToDictionary(c => c.ConnectionId);

        public Task<DataConnection> CreateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RegisterConnectionAsync(DataConnection connection)
            => throw new NotSupportedException();

        public Task<DataConnection?> GetConnectionAsync(string connectionId)
            => Guid.TryParse(connectionId, out var id)
                ? GetConnectionAsync(id)
                : Task.FromResult<DataConnection?>(null);

        public Task<DataConnection?> GetConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(_connections.GetValueOrDefault(connectionId));

        public Task<IEnumerable<DataConnection>> GetAllConnectionsAsync()
            => Task.FromResult<IEnumerable<DataConnection>>(_connections.Values);

        public Task<bool> RemoveConnectionAsync(string connectionId)
            => throw new NotSupportedException();

        public Task<bool> DeleteConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Dictionary<string, ConnectionHealthStatus>> TestAllConnectionsAsync()
            => throw new NotSupportedException();

        public Task<DataConnection?> GetConnectionByNameAsync(string connectionName, CancellationToken cancellationToken = default)
            => Task.FromResult(_connections.Values.FirstOrDefault(c => c.Name == connectionName));

        public Task<DataConnection?> GetConnectionAsync(string connectionId, CancellationToken cancellationToken)
            => GetConnectionAsync(connectionId);

        public Task UpdateHealthStatusAsync(string connectionId, bool isHealthy, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IEnumerable<DataConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IEnumerable<DataConnection>>(_connections.Values);

        public Task<DataConnection> UpdateConnectionAsync(DataConnection connection, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
