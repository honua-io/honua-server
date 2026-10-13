// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.SpatialAnalytics.Domain;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.SpatialAnalytics;

internal static class SpatialAnalyticsReadPolicyTestData
{
    internal const int TargetLayerId = 7;
    internal const int JoinLayerId = 9;

    internal static ClusterQuery KMeans()
        => new() { Algorithm = ClusterAlgorithm.KMeans, K = 2, MaxInputFeatures = 100 };

    internal static BufferAggregateQuery Buffer()
        => new() { Distance = 10d, Unit = DistanceUnit.Meters, Dissolve = true, MaxInputFeatures = 100 };

    internal static DensityQuery Density()
        => new() { Mode = DensityBinningMode.HexGrid, CellSizeMeters = 100d, MaxInputFeatures = 100 };

    internal static SpatialJoinQuery Join()
        => new() { JoinLayerId = JoinLayerId, Predicate = SpatialJoinPredicate.Intersects, MaxInputFeatures = 100 };

    internal static StatisticDefinition Sum(string field)
        => new()
        {
            StatisticType = StatisticType.Sum,
            OnStatisticField = field,
            OutStatisticFieldName = "total"
        };

    internal static FeatureQueryBuilder CreateQueryBuilder(string? schemaName = null)
    {
        var pool = new DefaultObjectPoolProvider().Create(new FeatureStoreStringBuilderPooledObjectPolicy());
        return schemaName is null
            ? new FeatureQueryBuilder(pool, new GeometryProcessor())
            : new FeatureQueryBuilder(pool, new GeometryProcessor(), schemaName);
    }

    internal sealed class Harness
    {
        private readonly RecordingFeatureDataAccess _dataAccess = new();
        private readonly IRowLevelSecurityFilterSource _rowPolicies = Substitute.For<IRowLevelSecurityFilterSource>();
        private readonly IFieldMaskSource _fieldMasks = Substitute.For<IFieldMaskSource>();
        private readonly Dictionary<int, MetadataV2Resource> _resources;

        public Harness(string? schemaName = null)
        {
            _resources = new Dictionary<int, MetadataV2Resource>
            {
                [TargetLayerId] = CreateResource(TargetLayerId),
                [JoinLayerId] = CreateResource(JoinLayerId),
            };

            _rowPolicies.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
                .Returns((SqlFragment?)null);
            _fieldMasks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
                .Returns(ImmutableArray<string>.Empty);
            var cacheManager = Substitute.For<IFeatureCacheManager>();
            cacheManager.GetGeometryStorageTypeAsync(Arg.Any<CancellationToken>())
                .Returns(GeometryStorageType.Geometry);

            var bindings = _resources.Select(pair => new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{pair.Key}", Name = $"binding-{pair.Key}" },
                ResourceId = pair.Value.Metadata.Id,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = $"public.layer_{pair.Key}",
                StorageLayerId = pair.Key,
            });
            var snapshot = new MetadataV2GraphSnapshot(
                new MetadataV2Graph { Revision = 1, Resources = [.. _resources.Values], StorageBindings = [.. bindings] },
                "\"analytics-read-policy\"",
                DateTimeOffset.UnixEpoch);

            FeatureStore = new PostgresFeatureStoreRefactored(
                CreateQueryBuilder(schemaName), _dataAccess, cacheManager,
                connectionProvider: null, dictionaryPool: null, connectionEncryptionService: null,
                v2Provider: new FixedMetadataV2GraphProvider(snapshot),
                rlsFilterSource: _rowPolicies, fieldMaskSource: _fieldMasks);

            Reader = new PostgresSpatialAnalyticsReader(
                CreateQueryBuilder(schemaName),
                _dataAccess,
                cacheManager,
                new FixedMetadataV2GraphProvider(snapshot),
                filterExpressionService: null,
                rlsFilterSource: _rowPolicies,
                fieldMaskSource: _fieldMasks);
        }

        public PostgresSpatialAnalyticsReader Reader { get; }

        public PostgresFeatureStoreRefactored FeatureStore { get; }

        public int TemporalReadCount => _dataAccess.TemporalReadCount;

        public string Sql => _dataAccess.Executed?.Sql ?? throw new InvalidOperationException("No query was executed.");

        public List<object> Parameters => _dataAccess.Executed?.WhereParameters ?? throw new InvalidOperationException("No query was executed.");

        public FeatureQuery ExecutedQuery => _dataAccess.ExecutedQuery ?? throw new InvalidOperationException("No query was executed.");

        public Harness WithRowPolicy(int layerId, string sql, params object?[] parameters)
        {
            _rowPolicies.ResolveAsync(_resources[layerId], Arg.Any<CancellationToken>())
                .Returns(new SqlFragment(sql, parameters));
            return this;
        }

        public Harness WithMaskedFields(int layerId, params string[] fields)
        {
            _fieldMasks.ResolveAsync(_resources[layerId], Arg.Any<CancellationToken>())
                .Returns([.. fields]);
            return this;
        }

        public void AssertNoDatabaseAccess()
            => _dataAccess.StatisticsCallCount.Should().Be(0);

        internal static MetadataV2Resource CreateResource(int layerId)
            => new()
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"res-{layerId}", Name = $"layer-{layerId}" },
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                Spatial = new MetadataV2ResourceSpatial { StorageCrs = new MetadataV2SpatialReference { Srid = 4326 } },
            };
    }

    /// <summary>
    /// Records the one analytics call the reader makes. <see cref="IFeatureDataAccess"/> is
    /// internal, so it cannot be proxied by a mocking library; every other member is inert.
    /// </summary>
    private sealed class RecordingFeatureDataAccess : IFeatureDataAccess
    {
        public ParameterizedQuery? Executed { get; private set; }

        public FeatureQuery? ExecutedQuery { get; private set; }

        public int StatisticsCallCount { get; private set; }

        public int TemporalReadCount { get; private set; }

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
        {
            StatisticsCallCount++;
            Executed = query;
            ExecutedQuery = featureQuery;
            return Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);
        }

        public Task<ImmutableArray<Feature>> ExecuteSelectQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<Feature>.Empty);

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
        {
            TemporalReadCount++;
            return Task.FromResult<TemporalExtentResult?>(TemporalExtentResult.Create(DateTimeOffset.UnixEpoch, DateTimeOffset.UtcNow));
        }

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

    private sealed class FixedMetadataV2GraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == snapshot.Revision ? snapshot : null);
    }
}
