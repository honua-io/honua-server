// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Raster.CogParser;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Raster.CogParser;

public sealed class CogPublicationBindingTests
{
    [UnitTest]
    public void Resolve_TwoServicesWithLayerZero_ResolvesByStorageIdAndFailsClosedOnAmbiguousIndex()
    {
        var snapshot = BuildTwoServiceRasterSnapshot();

        var first = CogPublicationBinding.Resolve(snapshot, 10);
        first.Should().NotBeNull();
        first!.Metadata.Id.Should().Be("pub-a");

        var second = CogPublicationBinding.Resolve(snapshot, 11);
        second.Should().NotBeNull();
        second!.Metadata.Id.Should().Be("pub-b");

        CogPublicationBinding.Resolve(snapshot, 0).Should().BeNull();
    }

    [UnitTest]
    public void Resolve_ExistingInstallWhereIndexEqualsStorageId_ResolvesMatchingPublication()
    {
        var snapshot = BuildSingleServiceSnapshot(layerIndex: 5, storageLayerId: 5);

        var resolved = CogPublicationBinding.Resolve(snapshot, 5);
        resolved.Should().NotBeNull();
        resolved!.Metadata.Id.Should().Be("pub-single");
    }

    [UnitTest]
    public void Resolve_UniqueLayerIndexWithoutMatchingStorageId_FallsBackToLayerIndex()
    {
        var snapshot = BuildSingleServiceSnapshot(layerIndex: 2, storageLayerId: 20);

        var byStorage = CogPublicationBinding.Resolve(snapshot, 20);
        byStorage.Should().NotBeNull();
        byStorage!.Metadata.Id.Should().Be("pub-single");

        var byIndex = CogPublicationBinding.Resolve(snapshot, 2);
        byIndex.Should().NotBeNull();
        byIndex!.Metadata.Id.Should().Be("pub-single");
    }

    [UnitTest]
    public void Resolve_AmbiguousStorageId_FailsClosedWithoutFallingBackToLayerIndex()
    {
        var graph = new MetadataV2Graph
        {
            Services = [Service("svc-a"), Service("svc-b")],
            Resources = [Resource("res-a", "bind-a"), Resource("res-b", "bind-b")],
            StorageBindings = [Binding("bind-a", "res-a", 10), Binding("bind-b", "res-b", 10)],
            Publications =
            [
                Publication("pub-a", "a", layerIndex: 10),
                Publication("pub-b", "b", layerIndex: 1),
            ],
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "\"cog-ambiguous-storage\"", DateTimeOffset.UtcNow);

        CogPublicationBinding.Resolve(snapshot, 10).Should().BeNull();
    }

    private static MetadataV2GraphSnapshot BuildTwoServiceRasterSnapshot()
    {
        var graph = new MetadataV2Graph
        {
            Services =
            [
                Service("svc-a"),
                Service("svc-b"),
            ],
            Resources =
            [
                Resource("res-a", "bind-a"),
                Resource("res-b", "bind-b"),
            ],
            StorageBindings =
            [
                Binding("bind-a", "res-a", 10),
                Binding("bind-b", "res-b", 11),
            ],
            Publications =
            [
                Publication("pub-a", "a", layerIndex: 0),
                Publication("pub-b", "b", layerIndex: 0),
            ],
        };
        return new MetadataV2GraphSnapshot(graph, "\"cog-two-svc\"", DateTimeOffset.UtcNow);
    }

    private static MetadataV2GraphSnapshot BuildSingleServiceSnapshot(int layerIndex, int storageLayerId)
    {
        var graph = new MetadataV2Graph
        {
            Services = [Service("svc-single")],
            Resources = [Resource("res-single", "bind-single")],
            StorageBindings = [Binding("bind-single", "res-single", storageLayerId)],
            Publications = [Publication("pub-single", "single", layerIndex)],
        };
        return new MetadataV2GraphSnapshot(graph, "\"cog-single\"", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Service Service(string id) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        Protocols = [ServiceProtocols.ImageServer],
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };

    private static MetadataV2Object Resource(string id, string bindingId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        Type = MetadataV2ObjectType.RasterDataset,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        StorageBindingIds = [bindingId],
        PrimaryStorageBindingId = bindingId,
    };

    private static MetadataV2StorageBinding Binding(string id, string resourceId, int storageLayerId) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ResourceId = resourceId,
        StorageType = MetadataV2StorageType.RelationalTable,
        Locator = id,
        StorageLayerId = storageLayerId,
    };

    private static MetadataV2Layer Publication(string id, string suffix, int layerIndex) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        ServiceId = $"svc-{suffix}",
        ResourceId = $"res-{suffix}",
        StorageBindingId = $"bind-{suffix}",
        PublicationType = MetadataV2LayerType.EsriImageLayer,
        IsPrimary = true,
        LayerIndex = layerIndex,
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
    };
}
