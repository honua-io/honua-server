// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.MySql.Features.FeatureStore;
using Honua.MySql.Features.FeatureStore.Services;
using Honua.MySql.Features.Infrastructure;

namespace Honua.MySql.Tests;

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
        }

        Assert.NotNull(dataAccess.LastSql);
        Assert.Contains("ORDER BY `name` ASC", dataAccess.LastSql, StringComparison.Ordinal);
        Assert.DoesNotContain("`id` ASC", dataAccess.LastSql, StringComparison.Ordinal);
    }

    private static MySqlFeatureStore CreateStore(IFeatureDataAccess dataAccess)
    {
        var mapping = new MySqlLayerMapping
        {
            LayerId = LayerId,
            TableName = "parcels",
            GeometryColumn = "geom",
            PrimaryKeyColumn = "id",
            Srid = 4326,
            AttributeColumns = ["name"],
            GeometryType = GeometryType.Point
        };
        var registry = new MySqlLayerMappingRegistry([mapping]);
        var queryBuilder = new MySqlFeatureQueryBuilder(registry);
        return new MySqlFeatureStore(queryBuilder, dataAccess);
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

        public Task<byte[]?> GetMvtTileAsync(int layerId, ParameterizedQuery query, CancellationToken cancellationToken)
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
