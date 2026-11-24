// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.FeatureStore;
using Honua.Db.Postgres.Features.FeatureStore.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.ObjectPool;
using NSubstitute;

namespace Honua.Postgres.Tests.Features.FeatureStore;

public sealed class PostgresFeatureStoreSecurityHardeningTests
{
    [UnitTest]
    public async Task QueryRelatedAsync_WhenOriginForeignKeyIsMasked_ThrowsArgumentException()
    {
        var originResource = CreateResource(layerId: 10);
        var relatedResource = CreateResource(layerId: 20);
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(originResource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("SecretKey"));
        maskSource.ResolveAsync(relatedResource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);

        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess, maskSource, originResource, relatedResource);
        var query = RelatedQuery.ForObjects([1L], 20, "secretkey", "parent_id");

        var act = () => store.QueryRelatedAsync(10, query);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*relationship key*");
        dataAccess.QueryRelatedCallCount.Should().Be(0);
    }

    [UnitTest]
    public async Task QueryRelatedAsync_WhenDestinationForeignKeyIsMasked_ThrowsArgumentException()
    {
        var originResource = CreateResource(layerId: 10);
        var relatedResource = CreateResource(layerId: 20);
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(originResource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray<string>.Empty);
        maskSource.ResolveAsync(relatedResource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("Parent_Id"));

        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess, maskSource, originResource, relatedResource);
        var query = RelatedQuery.ForObjects([1L], 20, "objectid", "parent_id");

        var act = () => store.QueryRelatedAsync(10, query);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*relationship key*");
        dataAccess.QueryRelatedCallCount.Should().Be(0);
    }

    [UnitTest]
    public async Task UpdateAsync_WithMaskedFields_MergesPreservedAttributeNames()
    {
        var resource = CreateResource(layerId: 10);
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(resource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("ssn", "salary"));

        var dataAccess = new RecordingFeatureDataAccess();
        var store = CreateStore(dataAccess, maskSource, resource);
        var incoming = Feature.Create(
            5,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "updated")) with
        {
            PreservedAttributeNames = ["existing_field", "SSN"]
        };

        await store.UpdateAsync(10, incoming);

        dataAccess.LastUpdatedFeature.Should().NotBeNull();
        dataAccess.LastUpdatedFeature!.Value.PreservedAttributeNames.Should().BeEquivalentTo(["existing_field", "SSN", "salary"]);
    }

    private static MetadataV2Object CreateResource(int layerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = $"res-{layerId}", Name = $"layer-{layerId}" },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        Spatial = new MetadataV2ObjectSpatial { StorageCrs = new MetadataV2SpatialReference { Srid = 4326 } }
    };

    private static PostgresFeatureStoreRefactored CreateStore(
        IFeatureDataAccess dataAccess,
        IFieldMaskSource fieldMaskSource,
        params MetadataV2Object[] resources)
    {
        var bindings = resources.Select(resource =>
        {
            var layerId = int.Parse(resource.Metadata.Id["res-".Length..], System.Globalization.CultureInfo.InvariantCulture);
            return new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata { Id = $"binding-{layerId}", Name = $"binding-{layerId}" },
                ResourceId = resource.Metadata.Id,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = $"public.layer_{layerId}",
                StorageLayerId = layerId
            };
        }).ToArray();

        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph { Revision = 1, Resources = resources, StorageBindings = bindings },
            "\"security-tests\"",
            DateTimeOffset.UnixEpoch);
        IMetadataV2GraphProvider v2Provider = new StubMetadataV2GraphProvider(snapshot);

        return new PostgresFeatureStoreRefactored(
            new FeatureQueryBuilder(new DefaultObjectPoolProvider().CreateStringBuilderPool(), new GeometryProcessor()),
            dataAccess,
            Substitute.For<IFeatureCacheManager>(),
            connectionProvider: null,
            dictionaryPool: null,
            connectionEncryptionService: null,
            v2Provider: v2Provider,
            fieldMaskSource: fieldMaskSource);
    }

    private sealed class StubMetadataV2GraphProvider(MetadataV2GraphSnapshot snapshot) : IMetadataV2GraphProvider
    {
        public ValueTask<MetadataV2GraphSnapshot> GetCurrentAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(snapshot);

        public ValueTask<MetadataV2GraphSnapshot?> GetByRevisionAsync(long revision, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<MetadataV2GraphSnapshot?>(revision == snapshot.Graph.Revision ? snapshot : null);
    }

    private sealed class RecordingFeatureDataAccess : IFeatureDataAccess
    {
        public int QueryRelatedCallCount { get; private set; }

        public Feature? LastUpdatedFeature { get; private set; }

        public Task<ImmutableArray<IReadOnlyDictionary<string, object?>>> ExecuteStatisticsQueryAsync(
            ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
            => Task.FromResult(ImmutableArray<IReadOnlyDictionary<string, object?>>.Empty);

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

        public Task<ImmutableArray<KmlFeature>> ExecuteSelectKmlQueryAsync(ParameterizedQuery query, FeatureQuery featureQuery, int layerId, CancellationToken cancellationToken)
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
        {
            QueryRelatedCallCount++;
            return Task.FromResult(QueryResult<Feature>.Empty());
        }

        public Task<Feature> CreateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<Feature> UpdateFeatureAsync(int layerId, Feature feature, CancellationToken cancellationToken)
        {
            LastUpdatedFeature = feature;
            return Task.FromResult(feature);
        }

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
}
