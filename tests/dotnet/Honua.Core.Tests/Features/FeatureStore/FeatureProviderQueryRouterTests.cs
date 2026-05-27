// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Moq;

namespace Honua.Core.Tests.Features.FeatureStore;

public sealed class FeatureProviderQueryRouterTests
{
    [Fact]
    public async Task ResolveReaderAsync_MetadataConnectionAndStorageBinding_ReturnsConnectionProviderReader()
    {
        var connectionId = Guid.NewGuid();
        var reader = new Mock<IFeatureReader>(MockBehavior.Strict).Object;
        var provider = CreateProvider(DataProviderNames.DuckDb, FeatureProviderCapabilities.ReadOnlyAnalytical, reader);
        var router = CreateRouter(connectionId, DataProviderNames.DuckDb, provider);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var resolved = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId: 1,
            FeatureProviderReadOperation.Query);

        resolved.Should().BeSameAs(reader);
    }

    [Fact]
    public async Task ResolveReaderAsync_UnsupportedStatisticsOperation_ThrowsClearError()
    {
        var connectionId = Guid.NewGuid();
        var reader = new Mock<IFeatureReader>(MockBehavior.Strict).Object;
        var capabilities = FeatureProviderCapabilities.ReadOnlyAnalytical with
        {
            SupportsStatistics = false
        };
        var provider = CreateProvider(DataProviderNames.DuckDb, capabilities, reader);
        var router = CreateRouter(connectionId, DataProviderNames.DuckDb, provider);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString());

        var action = () => router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId: 1,
            FeatureProviderReadOperation.Statistics);

        var exception = await action.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("does not support statistics operations");
    }

    [Fact]
    public async Task ResolveReaderAsync_BindableProvider_ReceivesMetadataV2Binding()
    {
        var connectionId = Guid.NewGuid();
        var reader = new Mock<IFeatureReader>(MockBehavior.Strict).Object;
        var provider = new CapturingBindableProvider(reader);
        var router = CreateRouter(connectionId, DataProviderNames.Postgis, provider);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId.ToString(), providerName: DataProviderNames.Postgis);

        var resolved = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            storageLayerId: 1,
            FeatureProviderReadOperation.Query);

        resolved.Should().BeSameAs(reader);
        provider.LastBinding.Should().NotBeNull();
        provider.LastBinding!.Resource.Should().BeSameAs(resource);
        provider.LastBinding.StorageMapping.QualifiedName.Should().Be("public.roads");
        provider.LastBinding.StorageMapping.PrimaryKeyColumn.Should().Be(FieldNames.ObjectId);
        provider.LastBinding.StorageLayerId.Should().Be(1);
    }

    private static FeatureProviderQueryRouter CreateRouter(
        Guid connectionId,
        string connectionProvider,
        IFeatureDataProvider provider)
    {
        var connection = new DataConnection
        {
            ConnectionId = connectionId,
            Name = "warehouse",
            Host = "warehouse.example.com",
            Port = 443,
            DatabaseName = "analytics",
            Username = "honua",
            Provider = connectionProvider,
            SecretRef = "env:HONUA_TEST_CONNECTION",
            SecretType = "environment",
            CreatedBy = "test"
        };

        return new FeatureProviderQueryRouter(
            new StubSecureConnectionRegistry(connection),
            new FeatureDataProviderRegistry([provider]));
    }

    private static IFeatureDataProvider CreateProvider(
        string providerName,
        FeatureProviderCapabilities capabilities,
        IFeatureReader reader)
    {
        var provider = new Mock<IFeatureDataProvider>(MockBehavior.Strict);
        provider.SetupGet(dataProvider => dataProvider.ProviderName).Returns(providerName);
        provider.SetupGet(dataProvider => dataProvider.Capabilities).Returns(capabilities);
        provider.SetupGet(dataProvider => dataProvider.Reader).Returns(reader);
        return provider.Object;
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot(string connectionId, string providerName = DataProviderNames.DuckDb)
    {
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "svc-maps", Name = "Maps" },
            SpatialReference = MetadataV2SpatialReference.Wgs84
        };
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-roads", Name = "roads" },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = ["binding-roads"],
            SchemaFields =
            [
                new MetadataV2Field
                {
                    Name = FieldNames.ObjectId,
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "shape",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.LineString,
                PrimaryGeometryField = "shape"
            }
        };
        var binding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-roads", Name = "binding-roads" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "public.roads",
            StorageLayerId = 1
        };
        var connection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId, Name = "warehouse" },
            Provider = providerName
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-roads", Name = "roads" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = binding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = "1", IsNumeric = true }
        };
        var graph = new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            Resources = [resource],
            Connections = [connection],
            StorageBindings = [binding],
            Services = [service],
            Publications = [publication]
        };

        return (new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow), service, resource, publication);
    }

    private sealed class CapturingBindableProvider(IFeatureReader reader) : IFeatureDataProvider, IBindableFeatureDataProvider
    {
        public FeatureProviderBinding? LastBinding { get; private set; }

        public string ProviderName => DataProviderNames.Postgis;

        public FeatureProviderCapabilities Capabilities => FeatureProviderCapabilities.ReadOnlyAnalytical;

        public IFeatureReader Reader => throw new NotSupportedException();

        public IFeatureWriter? Writer => null;

        public IFeatureReader CreateReaderForBinding(FeatureProviderBinding binding)
        {
            LastBinding = binding;
            return reader;
        }
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
            => throw new NotSupportedException();

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
