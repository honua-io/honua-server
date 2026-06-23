// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.ArcGisRest.Features.FeatureStore;
using Honua.ArcGisRest.Features.FeatureStore.Models;
using Honua.ArcGisRest.Features.FeatureStore.Services;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;

namespace Honua.ArcGisRest.Tests;

/// <summary>
/// Regression coverage for #2011: the federated provider must page across the
/// upstream <c>maxRecordCount</c> via <c>resultOffset</c> instead of surfacing
/// only the first <c>/query</c> page when <c>exceededTransferLimit</c> is set.
/// </summary>
public sealed class ArcGisRestPaginationTests
{
    private const int LayerId = 1;
    private const string ServiceUrl = "https://services.arcgis.com/org/arcgis/rest/services/parcels/FeatureServer";

    [Fact]
    public async Task QueryAsync_NoCallerLimit_PagesUntilUpstreamStopsFlaggingMore()
    {
        // Upstream maxRecordCount is 2: each page returns at most 2 features and
        // flags exceededTransferLimit until the final, short page. The provider
        // must loop on resultOffset and concatenate every page.
        var client = new PagingArcGisRestFeatureClient(upstreamMaxRecordCount: 2, totalFeatures: 5);
        var reader = CreateReader(client);

        var result = await reader.QueryAsync(LayerId, new FeatureQuery());

        Assert.Equal(5, result.Items.Length);
        Assert.False(result.HasMoreResults, "All records were drained; no more results remain.");
        Assert.Equal(5, result.TotalCount);
        // 0..1 (offset 0), 2..3 (offset 2), 4 (offset 4) => three requests, last short page stops the loop.
        Assert.Equal(3, client.QueryRequests.Count);
        Assert.Contains("resultOffset=2", client.QueryRequests[1], StringComparison.Ordinal);
        Assert.Contains("resultOffset=4", client.QueryRequests[2], StringComparison.Ordinal);
        Assert.Equal<long>([0, 1, 2, 3, 4], [.. result.Items.Select(f => f.Id)]);
    }

    [Fact]
    public async Task QueryAsync_WithCallerLimit_HonorsLimitAndDoesNotOverFetch()
    {
        // Caller asked for 3 records; upstream pages at 2. The provider must fetch
        // exactly enough pages to satisfy the limit (2 + 1) and stop — never
        // pulling the full result set.
        var client = new PagingArcGisRestFeatureClient(upstreamMaxRecordCount: 2, totalFeatures: 100);
        var reader = CreateReader(client);

        var result = await reader.QueryAsync(LayerId, new FeatureQuery { Limit = 3 });

        Assert.Equal(3, result.Items.Length);
        Assert.True(result.HasMoreResults, "More records remain beyond the caller's limit.");
        Assert.Equal(2, client.QueryRequests.Count);
        // Page 1 requests min(limit, DefaultPageSize) = 3 but upstream caps at 2;
        // page 2 requests the remaining 1.
        Assert.Contains("resultRecordCount=3", client.QueryRequests[0], StringComparison.Ordinal);
        Assert.Contains("resultRecordCount=1", client.QueryRequests[1], StringComparison.Ordinal);
        Assert.Equal<long>([0, 1, 2], [.. result.Items.Select(f => f.Id)]);
    }

    [Fact]
    public async Task QueryAsync_WithCallerOffset_PagesFromTheRequestedWindow()
    {
        var client = new PagingArcGisRestFeatureClient(upstreamMaxRecordCount: 2, totalFeatures: 7);
        var reader = CreateReader(client);

        var result = await reader.QueryAsync(LayerId, new FeatureQuery { Offset = 3 });

        // Starting at offset 3, the upstream yields ids 3,4,5,6 across pages.
        Assert.Equal<long>([3, 4, 5, 6], [.. result.Items.Select(f => f.Id)]);
        Assert.False(result.HasMoreResults);
        Assert.StartsWith(ServiceUrl, client.QueryRequests[0], StringComparison.Ordinal);
        Assert.Contains("resultOffset=3", client.QueryRequests[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryAsync_UpstreamAlwaysFlagsMoreButReturnsEmptyPage_TerminatesWithoutHang()
    {
        // A hostile/buggy upstream keeps flagging exceededTransferLimit but returns
        // no features. The empty-page guard must break the loop rather than spin.
        var client = new EmptyButFlaggingArcGisRestFeatureClient();
        var reader = CreateReader(client);

        var result = await reader.QueryAsync(LayerId, new FeatureQuery());

        Assert.Empty(result.Items);
        Assert.Equal(1, client.QueryCallCount);
    }

    [Fact]
    public async Task QueryObjectIdsAsync_ShortPage_ReturnsAllIdsInOneRequest()
    {
        // returnIdsOnly typically returns every matching id in a single response
        // (ignoring maxRecordCount). A page shorter than the requested page size
        // terminates the loop after one round-trip without truncation.
        var client = new PagingArcGisRestFeatureClient(upstreamMaxRecordCount: 1000, totalFeatures: 5);
        var reader = CreateReader(client);

        var ids = await reader.QueryObjectIdsAsync(LayerId, new FeatureQuery());

        Assert.Equal<long>([0, 1, 2, 3, 4], [.. ids]);
        Assert.Single(client.ObjectIdsRequests);
    }

    [Fact]
    public async Task QueryObjectIdsAsync_WithCallerLimit_TrimsToLimit()
    {
        var client = new PagingArcGisRestFeatureClient(upstreamMaxRecordCount: 1000, totalFeatures: 50);
        var reader = CreateReader(client);

        var ids = await reader.QueryObjectIdsAsync(LayerId, new FeatureQuery { Limit = 3 });

        Assert.Equal<long>([0, 1, 2], [.. ids]);
    }

    [Fact]
    public async Task QueryObjectIdsAsync_FullPageUpstream_LoopsUntilShortPage()
    {
        // Some servers do honor resultRecordCount on returnIdsOnly. When a page
        // returns exactly the requested DefaultPageSize, the provider must advance
        // the offset and fetch again until a short page arrives. Total spans one
        // full page plus a partial page.
        var totalIds = ArcGisRestFeatureStore.DefaultPageSize + 3;
        var client = new PagingObjectIdsClient(totalIds);
        var reader = CreateReader(client);

        var ids = await reader.QueryObjectIdsAsync(LayerId, new FeatureQuery());

        Assert.Equal(totalIds, ids.Length);
        Assert.Equal(0, ids[0]);
        Assert.Equal(totalIds - 1, ids[^1]);
        Assert.Equal(2, client.Requests.Count);
        Assert.Contains("resultOffset=" + ArcGisRestFeatureStore.DefaultPageSize, client.Requests[1], StringComparison.Ordinal);
    }

    private static IFeatureReader CreateReader(IArcGisRestFeatureClient client)
    {
        var connection = CreateConnection();
        var provider = new ArcGisRestFeatureStore(client);
        return ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(CreateBinding(provider, connection));
    }

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
                }
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

    /// <summary>
    /// Fake client that simulates an upstream FeatureServer with a fixed
    /// <c>maxRecordCount</c>: it honors <c>resultOffset</c>/<c>resultRecordCount</c>
    /// from the request URL and flags <c>exceededTransferLimit</c> whenever more
    /// rows remain past the returned page.
    /// </summary>
    private sealed class PagingArcGisRestFeatureClient(int upstreamMaxRecordCount, int totalFeatures) : IArcGisRestFeatureClient
    {
        public List<string> QueryRequests { get; } = [];

        public List<string> ObjectIdsRequests { get; } = [];

        public Task<ArcGisRestQueryResponse> QueryAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        {
            QueryRequests.Add(url);
            var (offset, recordCount) = ParsePaging(url);
            var pageSize = Math.Min(recordCount, upstreamMaxRecordCount);
            var available = Math.Max(0, totalFeatures - offset);
            var take = Math.Min(pageSize, available);

            var features = new ArcGisRestFeature[take];
            for (var i = 0; i < take; i++)
            {
                var id = offset + i;
                features[i] = new ArcGisRestFeature
                {
                    Attributes = ParseAttributes($$"""{"objectid": {{id}}}"""),
                    Geometry = ParseElement("""{"x": 1, "y": 2}""")
                };
            }

            var moreRemain = offset + take < totalFeatures;
            return Task.FromResult(new ArcGisRestQueryResponse
            {
                ObjectIdFieldName = "objectid",
                ExceededTransferLimit = moreRemain,
                Features = features
            });
        }

        public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        {
            ObjectIdsRequests.Add(url);
            var (offset, recordCount) = ParsePaging(url);
            var pageSize = Math.Min(recordCount, upstreamMaxRecordCount);
            var available = Math.Max(0, totalFeatures - offset);
            var take = Math.Min(pageSize, available);

            var ids = new long[take];
            for (var i = 0; i < take; i++)
            {
                ids[i] = offset + i;
            }

            return Task.FromResult(new ArcGisRestObjectIdsResponse
            {
                ObjectIdFieldName = "objectid",
                ObjectIds = ids
            });
        }

        public Task<ArcGisRestCountResponse> QueryCountAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestCountResponse { Count = totalFeatures });

        public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestExtentResponse());

        public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestLayerResponse());

        private static (int Offset, int RecordCount) ParsePaging(string url)
        {
            var offset = 0;
            var recordCount = int.MaxValue;
            var query = url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var key = pair[..eq];
                var value = pair[(eq + 1)..];
                if (key == "resultOffset" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o))
                {
                    offset = o;
                }
                else if (key == "resultRecordCount" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                {
                    recordCount = r;
                }
            }

            return (offset, recordCount);
        }
    }

    /// <summary>
    /// Fake client that honors <c>resultRecordCount</c>/<c>resultOffset</c> on
    /// returnIdsOnly requests, returning full <see cref="ArcGisRestFeatureStore.DefaultPageSize"/>
    /// pages until the tail partial page — exercising the object-ids pagination loop.
    /// </summary>
    private sealed class PagingObjectIdsClient(int totalIds) : IArcGisRestFeatureClient
    {
        public List<string> Requests { get; } = [];

        public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        {
            Requests.Add(url);
            var (offset, recordCount) = ParsePagingParams(url);
            var available = Math.Max(0, totalIds - offset);
            var take = Math.Min(recordCount, available);

            var ids = new long[take];
            for (var i = 0; i < take; i++)
            {
                ids[i] = offset + i;
            }

            return Task.FromResult(new ArcGisRestObjectIdsResponse
            {
                ObjectIdFieldName = "objectid",
                ObjectIds = ids
            });
        }

        public Task<ArcGisRestQueryResponse> QueryAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestQueryResponse());

        public Task<ArcGisRestCountResponse> QueryCountAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestCountResponse { Count = totalIds });

        public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestExtentResponse());

        public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestLayerResponse());

        private static (int Offset, int RecordCount) ParsePagingParams(string url)
        {
            var offset = 0;
            var recordCount = int.MaxValue;
            var query = url[(url.IndexOf('?', StringComparison.Ordinal) + 1)..];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=', StringComparison.Ordinal);
                if (eq < 0)
                {
                    continue;
                }

                var key = pair[..eq];
                var value = pair[(eq + 1)..];
                if (key == "resultOffset" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o))
                {
                    offset = o;
                }
                else if (key == "resultRecordCount" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r))
                {
                    recordCount = r;
                }
            }

            return (offset, recordCount);
        }
    }

    /// <summary>
    /// Fake client whose <c>/query</c> always flags <c>exceededTransferLimit</c> but
    /// never returns a feature — exercises the infinite-loop guard.
    /// </summary>
    private sealed class EmptyButFlaggingArcGisRestFeatureClient : IArcGisRestFeatureClient
    {
        private int _queryCallCount;

        public int QueryCallCount => _queryCallCount;

        public Task<ArcGisRestQueryResponse> QueryAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queryCallCount);
            return Task.FromResult(new ArcGisRestQueryResponse
            {
                ObjectIdFieldName = "objectid",
                ExceededTransferLimit = true,
                Features = []
            });
        }

        public Task<ArcGisRestCountResponse> QueryCountAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestCountResponse { Count = 0 });

        public Task<ArcGisRestObjectIdsResponse> QueryObjectIdsAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestObjectIdsResponse());

        public Task<ArcGisRestExtentResponse> QueryExtentAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestExtentResponse());

        public Task<ArcGisRestLayerResponse> GetLayerMetadataAsync(string url, string? authorizationHeader, CancellationToken cancellationToken)
            => Task.FromResult(new ArcGisRestLayerResponse());
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
}
