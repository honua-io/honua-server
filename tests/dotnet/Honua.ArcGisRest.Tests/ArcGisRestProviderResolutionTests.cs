// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.ArcGisRest;
using Honua.ArcGisRest.Features.FeatureStore;
using Honua.ArcGisRest.Features.FeatureStore.Models;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;

namespace Honua.ArcGisRest.Tests;

/// <summary>
/// Verifies the federated ArcGIS REST provider plugs into the shared Metadata v2 provider
/// seam: discoverable by canonical name and aliases, advertises read-only capabilities,
/// and resolves through <see cref="FeatureProviderQueryRouter"/> for publications whose
/// connection selects the ArcGIS REST engine.
/// </summary>
public class ArcGisRestProviderResolutionTests
{
    private const int LayerId = 1;
    private const string ServiceUrl = "https://services.arcgis.com/org/arcgis/rest/services/parcels/FeatureServer";

    [Theory]
    [InlineData("arcgis-rest")]
    [InlineData("arcgis_rest")]
    [InlineData("ArcGisRest")]
    [InlineData("arcgis")]
    [InlineData("esri")]
    [InlineData("esri-rest")]
    [InlineData("esri-featureserver")]
    [InlineData("FeatureServer")]
    public void Registry_ResolvesProviderByCanonicalNameAndAliases(string providerName)
    {
        var provider = CreateProvider();
        var registry = new FeatureDataProviderRegistry([provider]);

        Assert.True(registry.TryGetProvider(providerName, out var resolved));
        Assert.Same(provider, resolved);
    }

    [Fact]
    public void Capabilities_AreReadOnlyAndQueryCountExtentOnly()
    {
        var provider = CreateProvider();
        var caps = provider.Capabilities;

        Assert.Equal(DataProviderNames.ArcGisRest, provider.ProviderName);
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
    public async Task DefaultReader_WithoutMetadataV2Binding_ThrowsClearError()
    {
        var provider = CreateProvider();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.Reader.CountAsync(LayerId, new FeatureQuery()));

        Assert.Contains("Metadata v2 provider binding", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryStatistics_OnArcGisRestProvider_Throws()
    {
        var provider = CreateProvider();
        var reader = provider.Reader;
        await Assert.ThrowsAsync<NotSupportedException>(
            () => reader.QueryStatisticsAsync(LayerId, new FeatureQuery()));
    }

    [Fact]
    public async Task CreateReaderForBinding_RoutesCountThroughHttpClientWithBoundConnection()
    {
        var connection = CreateConnection();
        var client = new RecordingArcGisRestFeatureClient
        {
            CountResponse = new ArcGisRestCountResponse { Count = 42 }
        };
        var provider = new ArcGisRestFeatureStore(client);

        var reader = ((IBindableFeatureDataProvider)provider)
            .CreateReaderForBinding(CreateBinding(provider, connection));

        var count = await reader.CountAsync(LayerId, FeatureQuery.WithWhere("STATE = 'WA'"));

        Assert.Equal(42, count);
        Assert.NotNull(client.LastCountUrl);
        Assert.StartsWith(ServiceUrl + "/" + LayerId + "/query?", client.LastCountUrl!, StringComparison.Ordinal);
        Assert.Contains("returnCountOnly=true", client.LastCountUrl, StringComparison.Ordinal);
        Assert.Contains("where=" + Uri.EscapeDataString("STATE = 'WA'"), client.LastCountUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_ProjectsArcGisFeaturesToCanonicalFeatures()
    {
        var connection = CreateConnection();
        var client = new RecordingArcGisRestFeatureClient
        {
            QueryResponse = new ArcGisRestQueryResponse
            {
                ObjectIdFieldName = "objectid",
                ExceededTransferLimit = true,
                Features =
                [
                    new ArcGisRestFeature
                    {
                        Attributes = ParseAttributes("""{"objectid": 7, "name": "alpha"}"""),
                        Geometry = ParseElement("""{"x": -122.4, "y": 47.6}""")
                    },
                    new ArcGisRestFeature
                    {
                        Attributes = ParseAttributes("""{"objectid": 8, "name": "beta"}"""),
                        Geometry = ParseElement("""{"x": -73.0, "y": 40.7}""")
                    }
                ]
            }
        };

        var provider = new ArcGisRestFeatureStore(client);
        var reader = ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(CreateBinding(provider, connection));

        var result = await reader.QueryAsync(LayerId, new FeatureQuery { Limit = 2, OutputSrid = 3857 });

        Assert.Equal(2, result.Items.Length);
        Assert.True(result.HasMoreResults, "ExceededTransferLimit should map onto HasMoreResults.");
        Assert.Equal(7, result.Items[0].Id);
        Assert.Equal(8, result.Items[1].Id);
        Assert.Equal((long)7, result.Items[0].Attributes["objectid"]);
        Assert.Equal("alpha", result.Items[0].Attributes["name"]);
        Assert.NotNull(result.Items[0].Geometry);
        Assert.NotNull(client.LastQueryUrl);
        Assert.Contains("resultRecordCount=2", client.LastQueryUrl!, StringComparison.Ordinal);
        Assert.Contains("outSR=3857", client.LastQueryUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExtentAsync_ReturnsExtentInResolvedSrid()
    {
        var connection = CreateConnection();
        var client = new RecordingArcGisRestFeatureClient
        {
            ExtentResponse = new ArcGisRestExtentResponse
            {
                Extent = new ArcGisRestExtent
                {
                    Xmin = -180,
                    Ymin = -90,
                    Xmax = 180,
                    Ymax = 90,
                    SpatialReference = new ArcGisRestSpatialReference { Wkid = 4326 }
                }
            }
        };
        var provider = new ArcGisRestFeatureStore(client);
        var reader = ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(CreateBinding(provider, connection));

        var extent = await reader.GetExtentAsync(LayerId);

        Assert.NotNull(extent);
        Assert.Equal(-180, extent!.Value.MinX);
        Assert.Equal(180, extent.Value.MaxX);
        Assert.Equal(4326, extent.Value.SpatialReference);
    }

    [Fact]
    public async Task QueryAsync_UpstreamError_Throws()
    {
        var connection = CreateConnection();
        var client = new RecordingArcGisRestFeatureClient
        {
            QueryResponse = new ArcGisRestQueryResponse
            {
                Error = new ArcGisRestError { Code = 400, Message = "Invalid where clause" }
            }
        };
        var provider = new ArcGisRestFeatureStore(client);
        var reader = ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(CreateBinding(provider, connection));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reader.QueryAsync(LayerId, FeatureQuery.WithWhere("INVALID")));

        Assert.Contains("400", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Invalid where clause", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRouter_RoutesArcGisConnection_ThroughBindableSeam()
    {
        var connection = CreateConnection();
        var client = new RecordingArcGisRestFeatureClient
        {
            CountResponse = new ArcGisRestCountResponse { Count = 5 }
        };
        var provider = new ArcGisRestFeatureStore(client);
        var providerRegistry = new FeatureDataProviderRegistry([provider]);
        var router = new FeatureProviderQueryRouter(
            new FakeSecureConnectionRegistry(connection),
            providerRegistry,
            DataProviderNames.ArcGisRest);
        var (snapshot, service, resource, publication) = CreateSnapshot(connection.ConnectionId);

        var reader = await router.ResolveReaderAsync(
            snapshot,
            service,
            resource,
            publication,
            LayerId,
            FeatureProviderReadOperation.Count);

        var count = await reader.CountAsync(LayerId, new FeatureQuery());

        Assert.NotSame(provider, reader);
        Assert.Equal(5, count);
        Assert.NotNull(client.LastCountUrl);
        Assert.Contains(ServiceUrl, client.LastCountUrl!, StringComparison.Ordinal);
    }

    private static ArcGisRestFeatureStore CreateProvider()
        => new(new RecordingArcGisRestFeatureClient());

    private static DataConnection CreateConnection()
        => new()
        {
            ConnectionId = Guid.NewGuid(),
            Provider = DataProviderNames.ArcGisRest,
            Name = "remote-parcels",
            ConnectionString = ServiceUrl
        };

    private static FeatureProviderBinding CreateBinding(ArcGisRestFeatureStore provider, DataConnection connection)
    {
        var (snapshot, service, resource, publication) = CreateSnapshot(connection.ConnectionId);
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
        CreateSnapshot(Guid connectionId)
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
                    Name = "objectid",
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
                GeometryType = MetadataV2GeometryType.Point,
                PrimaryGeometryField = "shape"
            }
        };
        var storageBinding = new MetadataV2StorageBinding
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
            ResourceId = resource.Metadata.Id,
            ConnectionId = connectionId.ToString(),
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = "remote.parcels",
            StorageLayerId = LayerId
        };
        var metadataConnection = new MetadataV2Connection
        {
            Metadata = new MetadataV2ObjectMetadata { Id = connectionId.ToString(), Name = "remote-parcels" },
            Provider = DataProviderNames.ArcGisRest
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

    private static Dictionary<string, JsonElement> ParseAttributes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }

    private static JsonElement ParseElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private sealed class RecordingArcGisRestFeatureClient : IArcGisRestFeatureClient
    {
        public ArcGisRestQueryResponse QueryResponse { get; set; } = new();

        public ArcGisRestCountResponse CountResponse { get; set; } = new() { Count = 0 };

        public ArcGisRestExtentResponse ExtentResponse { get; set; } = new();

        public ArcGisRestObjectIdsResponse ObjectIdsResponse { get; set; } = new();

        public ArcGisRestLayerResponse LayerResponse { get; set; } = new();

        public string? LastQueryUrl { get; private set; }

        public string? LastCountUrl { get; private set; }

        public string? LastExtentUrl { get; private set; }

        public string? LastObjectIdsUrl { get; private set; }

        public string? LastLayerMetadataUrl { get; private set; }

        public Task<ArcGisRestQueryResponse> QueryAsync(string url, CancellationToken cancellationToken)
        {
            LastQueryUrl = url;
            return Task.FromResult(QueryResponse);
        }

        public Task<ArcGisRestCountResponse> QueryCountAsync(string url, CancellationToken cancellationToken)
        {
            LastCountUrl = url;
            return Task.FromResult(CountResponse);
        }

        public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, CancellationToken cancellationToken)
        {
            LastExtentUrl = url;
            return Task.FromResult(ExtentResponse);
        }

        public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, CancellationToken cancellationToken)
        {
            LastObjectIdsUrl = url;
            return Task.FromResult(ObjectIdsResponse);
        }

        public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, CancellationToken cancellationToken)
        {
            LastLayerMetadataUrl = url;
            return Task.FromResult(LayerResponse);
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
