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
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.Db.Postgres.Features.SpatialAnalytics;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;
using FeatureStoreStringBuilderPooledObjectPolicy = Honua.Db.Postgres.Features.FeatureStore.Services.StringBuilderPooledObjectPolicy;

namespace Honua.Db.Postgres.Tests.Features.SpatialAnalytics;

/// <summary>
/// The spatial analytics reader must apply the same read policy as a direct feature
/// query against the same layer: the row policy narrows the rows every operation
/// aggregates, and masked fields are neither returned nor usable as operands. Spatial
/// join applies each layer's own policy to its own side.
/// </summary>
public sealed class PostgresSpatialAnalyticsReaderReadPolicyTests
{
    internal const int TargetLayerId = 7;
    internal const int JoinLayerId = 9;

    [UnitTest]
    public async Task QueryClustersAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
        harness.ExecutedQuery.EnforcedSqlFilter.Should().NotBeNull();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), Buffer());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
    }

    [UnitTest]
    public async Task QueryDensityAsync_WithRowPolicy_FiltersSourceRows()
    {
        var harness = new Harness().WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west");

        await harness.Reader.QueryDensityAsync(TargetLayerId, new FeatureQuery(), Density());

        harness.Sql.Should().Contain("attributes->>'region' = $");
        harness.Parameters.Should().Contain("west");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithRowPolicies_FiltersEachLayerWithItsOwnPolicy()
    {
        var harness = new Harness()
            .WithRowPolicy(TargetLayerId, "attributes->>'region' = @p0", "west")
            .WithRowPolicy(JoinLayerId, "attributes->>'owner' = @p0", "team-a");

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), Join());

        var sql = harness.Sql;
        sql.Should().Contain("attributes->>'region' = $");
        sql.Should().Contain("attributes->>'owner' = $");
        harness.Parameters.Should().Contain("west").And.Contain("team-a");

        // The target policy lives inside the target CTE; the join policy inside the
        // join-side derived table, so neither can bind to the other layer's rows.
        var joinSide = sql[sql.IndexOf("LEFT JOIN (SELECT", StringComparison.Ordinal)..];
        joinSide.Should().Contain("attributes->>'owner' = $");
        joinSide.Should().NotContain("attributes->>'region' = $");
    }

    [UnitTest]
    public async Task QueryClustersAsync_PerFeatureModeWithMaskedField_RemovesItFromAttributes()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        harness.Sql.Should().Contain("(attributes - $");
        harness.Parameters.OfType<string[]>().Should().ContainSingle()
            .Which.Should().Equal("secret");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithMaskedTargetField_RemovesItFromTargetAttributes()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), Join());

        harness.Sql.Should().Contain("(attributes - $");
        harness.Parameters.OfType<string[]>().Should().ContainSingle()
            .Which.Should().Equal("secret");
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_CarryFieldMaskedOnJoinLayer_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(JoinLayerId, "secret");
        var join = Join() with { CarryFields = ["name", "Secret"] };

        var act = () => harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("Secret");
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_JoinStatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(JoinLayerId, "secret");
        var join = Join() with { OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        (await act.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().Contain("secret");
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_CarryFieldMaskedOnlyOnTargetLayer_IsAllowed()
    {
        // Masks are per layer: a field masked on the target says nothing about a
        // same-named field on the join layer.
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var join = Join() with { CarryFields = ["secret"] };

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        harness.Sql.Should().Contain("array_agg(");
    }

    [UnitTest]
    public async Task QueryClustersAsync_StatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var cluster = KMeans() with { ReturnHullPerCluster = true, OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), cluster);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryClustersAsync_WhereOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var query = new FeatureQuery { Where = "secret = 'x'" };

        var act = () => harness.Reader.QueryClustersAsync(TargetLayerId, query, KMeans());

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_GroupByMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var buffer = Buffer() with { GroupByFields = ["secret"] };

        var act = () => harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), buffer);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryBufferAggregateAsync_StatisticOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var buffer = Buffer() with { OutStatistics = [Sum("secret")] };

        var act = () => harness.Reader.QueryBufferAggregateAsync(TargetLayerId, new FeatureQuery(), buffer);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QueryDensityAsync_WeightOnMaskedField_IsRejectedBeforeDatabaseAccess()
    {
        var harness = new Harness().WithMaskedFields(TargetLayerId, "secret");
        var density = Density() with { WeightField = "secret" };

        var act = () => harness.Reader.QueryDensityAsync(TargetLayerId, new FeatureQuery(), density);

        await act.Should().ThrowAsync<ArgumentException>();
        harness.AssertNoDatabaseAccess();
    }

    [UnitTest]
    public async Task QuerySpatialJoinAsync_WithoutPolicies_BuildsTheSameSqlAsBefore()
    {
        var harness = new Harness();
        var join = Join() with { CarryFields = ["name"], OutStatistics = [Sum("population")] };

        await harness.Reader.QuerySpatialJoinAsync(TargetLayerId, new FeatureQuery(), join);

        var expected = CreateQueryBuilder().BuildSpatialJoinQuery(TargetLayerId, new FeatureQuery(), join);
        harness.Sql.Should().Be(expected.Sql);
        harness.Parameters.Should().Equal(expected.WhereParameters);
    }

    [UnitTest]
    public async Task QueryClustersAsync_WithoutPolicies_BuildsTheSameSqlAsBefore()
    {
        var harness = new Harness();

        await harness.Reader.QueryClustersAsync(TargetLayerId, new FeatureQuery(), KMeans());

        var expected = CreateQueryBuilder().BuildClusterQuery(TargetLayerId, new FeatureQuery(), KMeans());
        harness.Sql.Should().Be(expected.Sql);
        harness.Sql.Should().NotContain("attributes - $");
    }

    internal static ClusterQuery KMeans()
        => new() { Algorithm = ClusterAlgorithm.KMeans, K = 2, MaxInputFeatures = 100 };

    private static BufferAggregateQuery Buffer()
        => new() { Distance = 10d, Unit = DistanceUnit.Meters, Dissolve = true, MaxInputFeatures = 100 };

    private static DensityQuery Density()
        => new() { Mode = DensityBinningMode.HexGrid, CellSizeMeters = 100d, MaxInputFeatures = 100 };

    internal static SpatialJoinQuery Join()
        => new() { JoinLayerId = JoinLayerId, Predicate = SpatialJoinPredicate.Intersects, MaxInputFeatures = 100 };

    private static StatisticDefinition Sum(string field)
        => new()
        {
            StatisticType = StatisticType.Sum,
            OnStatisticField = field,
            OutStatisticFieldName = "total"
        };

    private static FeatureQueryBuilder CreateQueryBuilder(string? schemaName = null)
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

        private static MetadataV2Resource CreateResource(int layerId)
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
            => Task.FromResult<TemporalExtentResult?>(null);

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
