// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
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

        var dataAccess = CreateDataAccess();
        var store = CreateStore(dataAccess, maskSource, originResource, relatedResource);
        var query = RelatedQuery.ForObjects([1L], 20, "secretkey", "parent_id");

        var act = () => store.QueryRelatedAsync(10, query);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*relationship key*");
        await dataAccess.DidNotReceive().QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>());
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

        var dataAccess = CreateDataAccess();
        var store = CreateStore(dataAccess, maskSource, originResource, relatedResource);
        var query = RelatedQuery.ForObjects([1L], 20, "objectid", "parent_id");

        var act = () => store.QueryRelatedAsync(10, query);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*relationship key*");
        await dataAccess.DidNotReceive().QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task UpdateAsync_WithMaskedFields_MergesPreservedAttributeNames()
    {
        var resource = CreateResource(layerId: 10);
        var maskSource = Substitute.For<IFieldMaskSource>();
        maskSource.ResolveAsync(resource, Arg.Any<CancellationToken>())
            .Returns(ImmutableArray.Create("ssn", "salary"));

        Feature? updatedFeature = null;
        var dataAccess = CreateDataAccess(feature => updatedFeature = feature);
        var store = CreateStore(dataAccess, maskSource, resource);
        var incoming = Feature.Create(
            5,
            null,
            ImmutableDictionary<string, object?>.Empty.Add("name", "updated")) with
        {
            PreservedAttributeNames = ["existing_field", "SSN"]
        };

        await store.UpdateAsync(10, incoming);

        updatedFeature.Should().NotBeNull();
        updatedFeature!.Value.PreservedAttributeNames.Should().BeEquivalentTo(["existing_field", "SSN", "salary"]);
    }

    private static IFeatureDataAccess CreateDataAccess(Action<Feature>? onUpdate = null)
    {
        var dataAccess = Substitute.For<IFeatureDataAccess>();
        dataAccess.QueryRelatedAsync(Arg.Any<int>(), Arg.Any<RelatedQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());
        dataAccess.UpdateFeatureAsync(Arg.Any<int>(), Arg.Any<Feature>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var feature = call.ArgAt<Feature>(1);
                onUpdate?.Invoke(feature);
                return Task.FromResult(feature);
            });
        return dataAccess;
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
}
