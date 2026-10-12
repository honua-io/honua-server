// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.MySql.Features.FeatureStore;
using Honua.Db.MySql.Features.FeatureStore.Services;
using Honua.Db.MySql.Features.Infrastructure;

namespace Honua.Db.MySql.Tests;

/// <summary>
/// Unit tests for <see cref="MySqlFeatureStore"/> behaviours that do not require a real
/// MySQL/MariaDB connection — primarily streaming-path budget handling.
/// </summary>
public class MySqlFeatureStoreTests
{
    private const int LayerId = 1;

    [Fact]
    public async Task StreamFeaturesAsync_WithZeroLimit_DoesNotQueryAndYieldsNothing()
    {
        // Limit = 0 is a "zero rows" budget (capability probes, metadata fetches that
        // reuse the streaming surface). The store must short-circuit before issuing a
        // SELECT — otherwise consumers stream rows the canonical query said it did not want.
        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess);

        var emitted = new List<Feature>();
        await foreach (var f in store.StreamFeaturesAsync(LayerId, new FeatureQuery { Limit = 0 }))
        {
            emitted.Add(f);
        }

        Assert.Empty(emitted);
        Assert.Equal(0, dataAccess.SelectCallCount);
    }

    [Fact]
    public async Task StreamFeatureBatchesAsync_WithZeroLimit_DoesNotQueryAndYieldsNothing()
    {
        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess);

        var batches = new List<IReadOnlyList<Feature>>();
        await foreach (var batch in store.StreamFeatureBatchesAsync(LayerId, new FeatureQuery { Limit = 0 }, batchSize: 100))
        {
            batches.Add(batch);
        }

        Assert.Empty(batches);
        Assert.Equal(0, dataAccess.SelectCallCount);
    }

    [Fact]
    public async Task StreamFeatureBatchesAsync_WithNoLimit_StreamsUntilEmpty()
    {
        // No limit means "stream all". The recording stub returns one full page then an
        // empty page; the store must consume both before terminating.
        var dataAccess = new RecordingFeatureDataAccess(
            pages:
            [
                ImmutableArray.Create(Feature.Create(1, null), Feature.Create(2, null)),
                ImmutableArray<Feature>.Empty
            ]);
        var store = CreateStore(dataAccess);

        var emitted = new List<Feature>();
        await foreach (var batch in store.StreamFeatureBatchesAsync(LayerId, new FeatureQuery(), batchSize: 2))
        {
            emitted.AddRange(batch);
        }

        Assert.Equal(2, emitted.Count);
        Assert.True(dataAccess.SelectCallCount >= 1);
    }

    [Fact]
    public async Task StreamFeatureBatchesAsync_WithoutOrderBy_EmitsPrimaryKeyOrderingSql()
    {
        // Paged streaming uses repeated LIMIT/OFFSET queries; without a deterministic ORDER BY,
        // MySQL/MariaDB result order between pages is unspecified, so the query builder injects
        // an ascending primary-key sort when LIMIT or OFFSET is set without a caller-supplied
        // OrderBy. This guarantees a stable page sequence for canonical IStreamingFeatureStore
        // consumers (export, OData, gRPC, GeoServices) that don't know to request a
        // MySQL-specific order.
        var dataAccess = new RecordingFeatureDataAccess(
            pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        var store = CreateStore(dataAccess);

        await foreach (var _ in store.StreamFeatureBatchesAsync(LayerId, new FeatureQuery(), batchSize: 1))
        {
            // Intentionally empty: draining the stream is only needed to trigger the SQL
            // build captured in dataAccess.LastSql, asserted below.
        }

        Assert.NotNull(dataAccess.LastSql);
        Assert.Contains("ORDER BY `id` ASC", dataAccess.LastSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryPageAsync_WithoutOrderBy_EmitsPrimaryKeyOrderingSql()
    {
        // QueryPageAsync builds a (Limit + 1) page query to detect HasMore without an exact
        // COUNT. Because LIMIT is always set on that page query, the builder injects the
        // primary-key ORDER BY automatically — this test guards against a future regression
        // that bypasses the builder for paginated reads.
        var dataAccess = new RecordingFeatureDataAccess(
            pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        var store = CreateStore(dataAccess);

        await store.QueryPageAsync(LayerId, new FeatureQuery { Limit = 5 });

        Assert.NotNull(dataAccess.LastSql);
        Assert.Contains("ORDER BY `id` ASC", dataAccess.LastSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryFlatGeobufAsync_Throws_NotSupportedException()
    {
        // The provider does not support native FlatGeobuf — capability flag is false and the
        // FeatureServer guard rejects f=fgb before the store is ever called. Returning null
        // here would let any direct caller produce an empty success response with the
        // FlatGeobuf media type, so the store throws as defense-in-depth instead.
        var store = CreateStore(new RecordingFeatureDataAccess());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(
            () => store.QueryFlatGeobufAsync(LayerId, new FeatureQuery()));

        Assert.Contains("FlatGeobuf", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StreamFeatureBatchesAsync_WithCallerOrderBy_PreservesCallerSort()
    {
        // Caller-supplied OrderBy is honored as-is — the caller already accepts responsibility
        // for stable ordering across pages, and overriding it would change query semantics.
        var dataAccess = new RecordingFeatureDataAccess(
            pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        var store = CreateStore(dataAccess);

        var query = new FeatureQuery
        {
            OrderBy = ImmutableArray.Create(OrderByClause.Asc("name"))
        };

        await foreach (var _ in store.StreamFeatureBatchesAsync(LayerId, query, batchSize: 1))
        {
            // Intentionally empty: draining the stream is only needed to trigger the SQL
            // build captured in dataAccess.LastSql, asserted below.
        }

        Assert.NotNull(dataAccess.LastSql);
        Assert.Contains("ORDER BY `name` ASC", dataAccess.LastSql, StringComparison.Ordinal);
        Assert.DoesNotContain("`id` ASC", dataAccess.LastSql, StringComparison.Ordinal);
    }

    public static TheoryData<string> ReadOperations => new() { "get", "query", "ids", "count", "extent", "estimates", "page", "stream" };

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithRowLevelSecurityPredicateResolved_IsRefusedBeforeQuerying(string operation)
    {
        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess, CreateResolver(
            rowFilter: new Honua.Core.Queries.Filters.SqlFragment("region = @p0", ["west"])));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(store, operation));

        Assert.Contains("row-level security", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL/MariaDB", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataAccess.SelectCallCount);
    }

    [Theory]
    [MemberData(nameof(ReadOperations))]
    public async Task Read_WithFieldMaskResolved_IsRefusedBeforeQuerying(string operation)
    {
        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess, CreateResolver(maskedFields: ["name"]));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => InvokeReadAsync(store, operation));

        Assert.Contains("field-mask", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MySQL/MariaDB", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, dataAccess.SelectCallCount);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("stream")]
    public async Task Read_WithNoPolicyResolved_EmitsSameSqlAsWithoutResolver(string operation)
    {
        var baseline = new RecordingFeatureDataAccess(pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        await InvokeReadAsync(CreateStore(baseline), operation);

        var withResolver = new RecordingFeatureDataAccess(pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        await InvokeReadAsync(CreateStore(withResolver, CreateResolver()), operation);

        Assert.NotNull(baseline.LastSql);
        Assert.Equal(baseline.LastSql, withResolver.LastSql);
        Assert.Null(withResolver.LastFeatureQuery!.Value.EnforcedSqlFilter);
    }

    [Fact]
    public async Task StreamFeatureBatchesAsync_WithPermanentFilter_AppliesItToPageQueries()
    {
        var dataAccess = new RecordingFeatureDataAccess(pages: [ImmutableArray.Create(Feature.Create(1, null))]);
        var mapping = CreateMapping();
        var store = new MySqlFeatureStore(
            new MySqlFeatureQueryBuilder(new MySqlLayerMappingRegistry([mapping])),
            dataAccess,
            new StubV2Provider(LayerId, permanentFilterExpression: "name = 'visible'"),
            new FixedFilterExpressionService(new Honua.Core.Queries.Filters.SqlFragment("`name` = @p0", ["visible"])));

        await foreach (var _ in store.StreamFeatureBatchesAsync(LayerId, new FeatureQuery(), batchSize: 1))
        {
            // Intentionally empty: draining the stream triggers the page query asserted below.
        }

        Assert.NotNull(dataAccess.LastFeatureQuery!.Value.EnforcedSqlFilter);
        Assert.Contains("`name` =", dataAccess.LastSql, StringComparison.Ordinal);
    }

    private static LayerReadSecurityResolver CreateResolver(
        Honua.Core.Queries.Filters.SqlFragment? rowFilter = null,
        string[]? maskedFields = null)
        => new(
            new StubV2Provider(LayerId, permanentFilterExpression: null),
            filterExpressionService: null,
            new StubRowFilterSource(rowFilter),
            new StubFieldMaskSource(maskedFields ?? []));

    private static async Task InvokeReadAsync(MySqlFeatureStore store, string operation)
    {
        switch (operation)
        {
            case "get":
                await store.GetAsync(LayerId, 1);
                break;
            case "query":
                await store.QueryAsync(LayerId, new FeatureQuery());
                break;
            case "ids":
                await store.QueryObjectIdsAsync(LayerId, new FeatureQuery());
                break;
            case "count":
                await store.CountAsync(LayerId, new FeatureQuery());
                break;
            case "extent":
                await store.GetExtentAsync(LayerId);
                break;
            case "estimates":
                await store.GetEstimatesAsync(LayerId);
                break;
            case "page":
                await store.QueryPageAsync(LayerId, new FeatureQuery { Limit = 5 });
                break;
            case "stream":
                await foreach (var _ in store.StreamFeaturesAsync(LayerId, new FeatureQuery()))
                {
                    // Intentionally empty: draining the stream triggers the read.
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
        }
    }

    private static MySqlFeatureStore CreateStore(IFeatureDataAccess dataAccess, LayerReadSecurityResolver? readSecurity = null)
    {
        var registry = new MySqlLayerMappingRegistry([CreateMapping()]);
        var queryBuilder = new MySqlFeatureQueryBuilder(registry);
        return new MySqlFeatureStore(queryBuilder, dataAccess, readSecurity: readSecurity);
    }

    private static MySqlLayerMapping CreateMapping() => new()
    {
        LayerId = LayerId,
        TableName = "parcels",
        GeometryColumn = "geom",
        PrimaryKeyColumn = "id",
        Srid = 4326,
        AttributeColumns = ["name"],
        GeometryType = GeometryType.Point
    };

    private sealed class FixedFilterExpressionService(Honua.Core.Queries.Filters.SqlFragment fragment) :
        Honua.Core.Queries.Filters.IFilterExpressionService
    {
        private static readonly Honua.Core.Queries.Filters.FilterExpression _expression =
            new Honua.Core.Queries.Filters.PropertyReference("name");

        public Honua.Core.Queries.Filters.FilterParseResult Parse(Honua.Core.Queries.Filters.FilterLanguage language, string? filter)
            => Honua.Core.Queries.Filters.FilterParseResult.Success(_expression);

        public Honua.Core.Queries.Filters.FilterParseResult ParseAndNormalize(
            Honua.Core.Queries.Filters.FilterLanguage language, string? filter, MetadataV2Resource resource)
            => Honua.Core.Queries.Filters.FilterParseResult.Success(_expression);

        public Honua.Core.Queries.Filters.FilterExpression Normalize(
            Honua.Core.Queries.Filters.FilterExpression expression, MetadataV2Resource resource)
            => expression;

        public Honua.Core.Queries.Filters.FilterTranslationResult Translate(
            Honua.Core.Queries.Filters.FilterExpression? expression, MetadataV2Resource resource)
            => Honua.Core.Queries.Filters.FilterTranslationResult.Success(expression, fragment);
    }

    private sealed class StubV2Provider(int storageLayerId, string? permanentFilterExpression) :
        Honua.Core.Features.Metadata.Abstractions.IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
        {
            var resource = new MetadataV2Resource
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "res-parcels", Name = "Parcels" },
                Type = MetadataV2ResourceType.FeatureDataset,
                PermanentFilter = permanentFilterExpression == null ? null : new MetadataV2PermanentFilter
                {
                    Expression = permanentFilterExpression,
                    Language = MetadataV2PermanentFilterLanguages.ArcGisSql
                }
            };
            var storageBinding = new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = "binding-parcels", Name = "binding-parcels" },
                ResourceId = resource.Metadata.Id,
                StorageLayerId = storageLayerId,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = "gis.parcels"
            };
            var graph = new MetadataV2Graph
            {
                Revision = 1,
                Environment = "test",
                Resources = [resource],
                StorageBindings = [storageBinding]
            };
            return ValueTask.FromResult(new MetadataV2GraphSnapshot(graph, "etag-stub", DateTimeOffset.UtcNow));
        }

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(null);
    }

    private sealed class StubRowFilterSource(Honua.Core.Queries.Filters.SqlFragment? fragment) :
        Honua.Core.Features.Authorization.Abstractions.IRowLevelSecurityFilterSource
    {
        public string? LastResourceId { get; private set; }

        public Task<Honua.Core.Queries.Filters.SqlFragment?> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
        {
            LastResourceId = resource.Metadata.Id;
            return Task.FromResult(fragment);
        }
    }

    private sealed class StubFieldMaskSource(string[] maskedFields) :
        Honua.Core.Features.Authorization.Abstractions.IFieldMaskSource
    {
        public string? LastResourceId { get; private set; }

        public Task<System.Collections.Immutable.ImmutableArray<string>> ResolveAsync(
            MetadataV2Resource resource, CancellationToken cancellationToken = default)
        {
            LastResourceId = resource.Metadata.Id;
            return Task.FromResult(System.Collections.Immutable.ImmutableArray.Create(maskedFields));
        }
    }

    private sealed class RecordingFeatureDataAccess : IFeatureDataAccess
    {
        private readonly Queue<ImmutableArray<Feature>> _pages;

        public RecordingFeatureDataAccess(IEnumerable<ImmutableArray<Feature>>? pages = null)
        {
            _pages = pages is null ? new Queue<ImmutableArray<Feature>>() : new Queue<ImmutableArray<Feature>>(pages);
        }

        public int SelectCallCount { get; private set; }

        public FeatureQuery? LastFeatureQuery { get; private set; }

        public string? LastSql { get; private set; }

        public Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        {
            SelectCallCount++;
            LastFeatureQuery = featureQuery;
            LastSql = query.Sql;
            var page = _pages.Count > 0 ? _pages.Dequeue() : ImmutableArray<Feature>.Empty;
            return Task.FromResult(page);
        }

        public Task<long> ExecuteCountQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(0L);

        public Task<ImmutableArray<ProjectedPoint>> ExecuteSelectProjectedPointsAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<ProjectedPoint>.Empty);

        public Task<ImmutableArray<GmlFeature>> ExecuteSelectGmlQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<GmlFeature>.Empty);

        public Task<ImmutableArray<EncodedGeoJsonFeature>> ExecuteSelectGeoJsonQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<EncodedGeoJsonFeature>.Empty);

        public Task<ImmutableArray<RawGeoJsonFeature>> ExecuteSelectRawGeoJsonQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<RawGeoJsonFeature>.Empty);

        public Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<KmlFeature>.Empty);

        public Task<byte[]?> ExecuteSelectFlatGeobufQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task<byte[]?> ExecuteSelectGeobufQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public Task<ImmutableArray<long>> ExecuteSelectObjectIdsQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<long>.Empty);

        public Task<QueryResult<Feature>> QueryRelatedAsync(int layerId, RelatedQuery query, CancellationToken cancellationToken)
            => Task.FromResult(QueryResult<Feature>.Empty());

        public Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<bool> DeleteFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Feature?> GetFeatureAsync(int layerId, long featureId, CancellationToken cancellationToken)
            => Task.FromResult<Feature?>(null);

        public Task<FeatureExtent?> GetExtentAsync(int layerId, ParameterizedQuery? query, FeatureQuery featureQuery, CancellationToken cancellationToken)
            => Task.FromResult<FeatureExtent?>(null);

        public Task<TemporalExtentResult?> GetTemporalExtentAsync(int layerId, ParameterizedQuery? query, CancellationToken cancellationToken)
            => Task.FromResult<TemporalExtentResult?>(null);

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

        public Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, long maxTileSize, CancellationToken cancellationToken)
            => Task.FromResult<byte[]?>(null);

        public async IAsyncEnumerable<Feature> StreamFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public async IAsyncEnumerable<GmlFeature> StreamGmlFeaturesAsync(int layerId, ParameterizedQuery query, FeatureQuery featureQuery, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
