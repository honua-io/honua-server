// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Oracle;
using Honua.Oracle.Features.FeatureStore;
using Honua.Oracle.Features.FeatureStore.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Oracle.Tests;

/// <summary>
/// Verifies that the Oracle provider plugs into the shared Metadata v2 provider seam:
/// it is discoverable by canonical name and aliases, advertises read-only capabilities,
/// and resolves through <see cref="FeatureProviderQueryRouter"/> for source-backed publications
/// whose connection selects the Oracle engine.
/// </summary>
public class OracleProviderResolutionTests
{
    private const int LayerId = 1;

    [Theory]
    [InlineData("oracle")]
    [InlineData("oracledb")]
    [InlineData("Oracle")]
    public void Registry_ResolvesProviderByCanonicalNameAndAliases(string providerName)
    {
        var oracleProvider = CreateOracleStore();
        var registry = new FeatureDataProviderRegistry([oracleProvider]);

        Assert.True(registry.TryGetProvider(providerName, out var resolved));
        Assert.Same(oracleProvider, resolved);
    }

    [Fact]
    public void Capabilities_AreReadOnlyWithNativeOutputsDisabled()
    {
        var provider = CreateOracleStore();

        var caps = provider.Capabilities;

        Assert.Equal(DataProviderNames.Oracle, provider.ProviderName);
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
    public async Task QueryStatistics_OnOracleProvider_Throws()
    {
        var provider = CreateOracleStore();
        var reader = provider.Reader;
        await Assert.ThrowsAsync<NotSupportedException>(
            () => reader.QueryStatisticsAsync(LayerId, new FeatureQuery()));
    }

    [Fact]
    public async Task QueryTopFeatures_OnOracleProvider_Throws()
    {
        var provider = CreateOracleStore();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.Reader.QueryTopFeaturesAsync(LayerId, new FeatureQuery()));
    }

    [Fact]
    public async Task GetTemporalExtent_OnOracleProvider_Throws()
    {
        var provider = CreateOracleStore();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => provider.Reader.GetTemporalExtentAsync(LayerId, "ts", TemporalPropertyType.DateTime));
    }

    [Fact]
    public async Task DefaultReader_WithoutMetadataV2Binding_ThrowsClearError()
    {
        var provider = CreateOracleStore();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.Reader.CountAsync(LayerId, new FeatureQuery()));

        Assert.Contains("Metadata v2 provider binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlatGeobuf_ReturnsNull_FallsBackToFormatter()
    {
        var provider = CreateOracleStore();

        var payload = await provider.Reader.QueryFlatGeobufAsync(LayerId, new FeatureQuery());

        Assert.Null(payload);
    }

    [Fact]
    public async Task CreateReaderForBinding_WithoutDataConnection_PassesNullDataConnection()
    {
        var factory = new RecordingConnectionFactory();
        var provider = CreateOracleStore(factory);
        var reader = ((IBindableFeatureDataProvider)provider)
            .CreateReaderForBinding(CreateBinding(provider, connection: null));

        Assert.NotSame(provider, reader);

        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(
            () => reader.CountAsync(LayerId, new FeatureQuery()));

        Assert.True(factory.WasCalled);
        Assert.Null(factory.LastDataConnection);
    }

    [Fact]
    public async Task CreateReaderForBinding_WithDataConnection_PassesItToConnectionFactory()
    {
        var connectionId = Guid.NewGuid();
        var dataConnection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = DataProviderNames.Oracle,
            Name = "secure",
            Host = "db.example",
            Port = 1521,
            DatabaseName = "ORCL",
            Username = "reader"
        };

        var factory = new RecordingConnectionFactory();
        var provider = CreateOracleStore(factory);
        var reader = ((IBindableFeatureDataProvider)provider)
            .CreateReaderForBinding(CreateBinding(provider, dataConnection));

        Assert.NotSame(provider, reader);

        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(
            () => reader.CountAsync(LayerId, new FeatureQuery()));

        Assert.NotNull(factory.LastDataConnection);
        Assert.Equal(connectionId, factory.LastDataConnection!.ConnectionId);
    }

    [Fact]
    public async Task QueryRouter_RoutesOracleConnection_ThroughBindableSeam()
    {
        var connectionId = Guid.NewGuid();
        var dataConnection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = DataProviderNames.Oracle,
            Name = "secure",
            Host = "db.example",
            Port = 1521,
            DatabaseName = "ORCL",
            Username = "reader"
        };

        var factory = new RecordingConnectionFactory();
        var provider = CreateOracleStore(factory);
        var providerRegistry = new FeatureDataProviderRegistry([provider]);
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(dataConnection),
            providerRegistry,
            DataProviderNames.Oracle);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            LayerId,
            FeatureProviderReadOperation.Count);

        Assert.NotSame(provider, reader);

        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(
            () => reader.CountAsync(LayerId, new FeatureQuery()));

        Assert.NotNull(factory.LastDataConnection);
        Assert.Equal(connectionId, factory.LastDataConnection!.ConnectionId);
    }

    [Fact]
    public async Task QueryRouter_RoutesOracleDbAlias_ThroughBindableSeam()
    {
        var connectionId = Guid.NewGuid();
        var dataConnection = new DataConnection
        {
            ConnectionId = connectionId,
            Provider = "oracledb",
            Name = "secure",
            Host = "db.example",
            Port = 1521,
            DatabaseName = "ORCL",
            Username = "reader"
        };

        var factory = new RecordingConnectionFactory();
        var provider = CreateOracleStore(factory);
        var providerRegistry = new FeatureDataProviderRegistry([provider]);
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(dataConnection),
            providerRegistry,
            DataProviderNames.Oracle);
        var (snapshot, service, resource, publication) = CreateSnapshot(connectionId, providerAlias: "oracledb");

        var reader = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            LayerId,
            FeatureProviderReadOperation.Count);

        await Assert.ThrowsAsync<RecordingConnectionFactory.SentinelException>(
            () => reader.CountAsync(LayerId, new FeatureQuery()));
    }

    private static FeatureProviderBinding CreateBinding(OracleFeatureStore provider, DataConnection? connection)
    {
        var (snapshot, service, resource, publication) = CreateSnapshot(connection?.ConnectionId ?? Guid.NewGuid());
        var storageBinding = snapshot.ResolveStorageBinding(publication)
            ?? throw new InvalidOperationException("Test snapshot did not include a storage binding.");

        return new FeatureProviderBinding(
            service,
            resource,
            publication,
            storageBinding,
            FeatureStorageMapping.FromMetadata(resource, storageBinding),
            LayerId,
            provider,
            connection);
    }

    private static (MetadataV2GraphSnapshot Snapshot, MetadataV2Service Service, MetadataV2Resource Resource, MetadataV2Publication Publication)
        CreateSnapshot(Guid connectionId, string providerAlias = DataProviderNames.Oracle)
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
                    Name = "OBJECTID",
                    Type = MetadataV2FieldType.BigInteger,
                    Nullable = false,
                    SemanticRoles = ["id.primary"]
                },
                new MetadataV2Field
                {
                    Name = "SHAPE",
                    Type = MetadataV2FieldType.Geometry,
                    Nullable = false,
                    SemanticRoles = ["geometry.primary"]
                },
                new MetadataV2Field { Name = "NAME", Type = MetadataV2FieldType.String }
            ],
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Polygon,
                PrimaryGeometryField = "SHAPE"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "GIS.PARCELS",
            StorageLayerId = LayerId
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "secure" },
            Provider = providerAlias
        };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "pub-parcels", Name = "Parcels" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            StorageBindingId = storageBinding.Metadata.Id,
            Identifier = new MetadataV2PublicationIdentifier { Value = LayerId.ToString(CultureInfo.InvariantCulture), IsNumeric = true }
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

    private static OracleFeatureStore CreateOracleStore(IOracleConnectionFactory? factory = null)
    {
        var connectionFactory = factory ?? new ThrowingConnectionFactory();
        var dataAccess = new OracleFeatureDataAccess(
            connectionFactory,
            Options.Create(new OracleOptions()),
            NullLogger<OracleFeatureDataAccess>.Instance);

        var probe = new AcceptingProbe();
        var guard = new OracleSpatialGuard(probe, NullLogger<OracleSpatialGuard>.Instance);

        return new OracleFeatureStore(dataAccess, guard);
    }

    /// <summary>
    /// Probe stub that approves any SDO_GEOMETRY column and reports no versioning. Allows
    /// resolution tests to exercise binding/connection flow without a real Oracle connection
    /// while still proving that the guard is invoked before the data access call.
    /// </summary>
    private sealed class AcceptingProbe : IOracleSpatialMetadataProbe
    {
        public Task<string?> GetGeometryColumnTypeAsync(OracleLayerMapping mapping, DataConnection? dataConnection, CancellationToken cancellationToken)
            => Task.FromResult<string?>("SDO_GEOMETRY");

        public Task<IReadOnlyList<string>> GetArcSdeVersioningColumnsAsync(OracleLayerMapping mapping, DataConnection? dataConnection, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    private sealed class ThrowingConnectionFactory : IOracleConnectionFactory
    {
        public Task<global::Oracle.ManagedDataAccess.Client.OracleConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Connection access not expected in resolution tests.");
    }

    private sealed class RecordingConnectionFactory : IOracleConnectionFactory
    {
        public DataConnection? LastDataConnection { get; private set; }

        public bool WasCalled { get; private set; }

        public Task<global::Oracle.ManagedDataAccess.Client.OracleConnection> OpenAsync(DataConnection? dataConnection, CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastDataConnection = dataConnection;
            throw new SentinelException();
        }

        public sealed class SentinelException : Exception
        {
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
