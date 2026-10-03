// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.Stac.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// STAC reads resolve a collection's storage-layer handle from the publication's own graph
/// entries, never from its protocol-facing layer index (SEC-4). The fixture publishes
/// <c>resource.parcels</c> as collection <see cref="CollectionLayerIndex"/> while its storage
/// handle is <see cref="ParcelsStorageLayerId"/>, and <c>resource.permits</c> owns
/// <see cref="CollectionLayerIndex"/> as its storage handle.
/// </summary>
public sealed class StacStorageLayerResolutionTests
{
    private const int CollectionLayerIndex = 3;
    private const int ParcelsStorageLayerId = 7;

    [UnitTest]
    public async Task ResolveAsync_PublicationWithoutItsOwnBinding_ReadsTheResourceStorageHandle()
    {
        var snapshot = Snapshot();
        var publication = snapshot.Index.PublicationsById["pub.parcels"];
        var reader = Substitute.For<IFeatureReader>();

        var resolution = await StacFeatureReaderResolver.ResolveAsync(
            new DefaultHttpContext(),
            reader,
            snapshot,
            snapshot.Index.ServicesById["service.stac"],
            snapshot.Index.ResourcesById["resource.parcels"],
            publication,
            CancellationToken.None);

        publication.LayerIndex.Should().Be(CollectionLayerIndex);
        resolution.Should().NotBeNull();
        resolution!.Value.StorageLayerId.Should().Be(ParcelsStorageLayerId);
        resolution.Value.Reader.Should().BeSameAs(reader);
    }

    [UnitTest]
    public async Task ResolveAsync_UnboundPublicationWhoseLayerIndexIsAnotherResourcesStorageId_ResolvesNothing()
    {
        var snapshot = Snapshot();

        var resolution = await StacFeatureReaderResolver.ResolveAsync(
            new DefaultHttpContext(),
            Substitute.For<IFeatureReader>(),
            snapshot,
            snapshot.Index.ServicesById["service.stac"],
            snapshot.Index.ResourcesById["resource.unbound"],
            snapshot.Index.PublicationsById["pub.unbound"],
            CancellationToken.None);

        resolution.Should().BeNull();
    }

    [UnitTest]
    public void ResolvedStacPublication_StorageLayerId_IsTheStorageHandleNotTheCollectionIndex()
    {
        var snapshot = Snapshot();
        var resolved = new StacV2Lookups.ResolvedStacPublication(
            snapshot.Index.PublicationsById["pub.parcels"],
            snapshot.Index.ResourcesById["resource.parcels"],
            snapshot.Index.ServicesById["service.stac"],
            CollectionLayerIndex,
            snapshot);

        resolved.StorageLayerId.Should().Be(ParcelsStorageLayerId);
    }

    [UnitTest]
    public async Task MapResourceToCollectionAsync_TemporalExtent_ReadsTheStorageHandle()
    {
        var snapshot = Snapshot();
        var instant = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var reader = Substitute.For<IFeatureReader>();
        reader.GetTemporalExtentAsync(ParcelsStorageLayerId, "timestamp", Arg.Any<TemporalPropertyType>(), Arg.Any<CancellationToken>())
            .Returns(TemporalExtentResult.Create(instant, instant));

        var collection = await StacMappingService.MapResourceToCollectionAsync(
            snapshot.Index.ResourcesById["resource.parcels"],
            snapshot.Index.PublicationsById["pub.parcels"],
            snapshot.Index.ServicesById["service.stac"],
            CollectionLayerIndex,
            ParcelsStorageLayerId,
            reader,
            "https://example.test",
            null,
            CancellationToken.None);

        collection.Id.Should().Be("3");
        collection.Extent.Temporal.Interval.Single().Should().OnlyContain(value => value != null);
        await reader.DidNotReceive().GetTemporalExtentAsync(
            CollectionLayerIndex, Arg.Any<string>(), Arg.Any<TemporalPropertyType>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task MapResourceToCollectionAsync_NoStorageHandle_SkipsTheFeatureStoreProbe()
    {
        var snapshot = Snapshot();
        var reader = Substitute.For<IFeatureReader>();

        var collection = await StacMappingService.MapResourceToCollectionAsync(
            snapshot.Index.ResourcesById["resource.unbound"],
            snapshot.Index.PublicationsById["pub.unbound"],
            snapshot.Index.ServicesById["service.stac"],
            CollectionLayerIndex,
            storageLayerId: null,
            reader,
            "https://example.test",
            null,
            CancellationToken.None);

        collection.Extent.Temporal.Interval.Single().Should().OnlyContain(value => value == null);
        await reader.DidNotReceiveWithAnyArgs().GetTemporalExtentAsync(
            default, default!, default, default);
    }

    private static MetadataV2GraphSnapshot Snapshot()
        => new(
            new MetadataV2Graph
            {
                Resources =
                [
                    FeatureResource("resource.parcels", "storage.parcels"),
                    FeatureResource("resource.permits", "storage.permits"),
                    FeatureResource("resource.unbound", storageBindingId: null),
                ],
                StorageBindings =
                [
                    Binding("storage.parcels", "resource.parcels", ParcelsStorageLayerId),
                    Binding("storage.permits", "resource.permits", CollectionLayerIndex),
                ],
                Services =
                [
                    new MetadataV2Service
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "service.stac", Name = "stac" },
                        Protocols = [ServiceProtocols.Stac],
                        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                    }
                ],
                Publications =
                [
                    Publication("pub.parcels", "resource.parcels"),
                    Publication("pub.unbound", "resource.unbound"),
                ],
            },
            "\"stac-storage\"",
            DateTimeOffset.UtcNow);

    private static MetadataV2Resource FeatureResource(string id, string? storageBindingId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Type = MetadataV2ResourceType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageBindingIds = storageBindingId is null ? [] : [storageBindingId],
            PrimaryStorageBindingId = storageBindingId,
            SchemaFields = [new MetadataV2Field { Name = "timestamp", Type = MetadataV2FieldType.DateTime }],
        };

    private static MetadataV2StorageBinding Binding(string id, string resourceId, int storageLayerId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            ResourceId = resourceId,
            StorageType = MetadataV2StorageType.RelationalTable,
            Locator = id,
            StorageLayerId = storageLayerId,
        };

    /// <summary>
    /// A STAC collection publication with no storage binding of its own, so the handle is
    /// resolved from its resource.
    /// </summary>
    private static MetadataV2Publication Publication(string id, string resourceId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ResourceId = resourceId,
            ServiceId = "service.stac",
            PublicationType = MetadataV2PublicationType.StacCollection,
            IsPrimary = true,
            LayerIndex = CollectionLayerIndex,
        };
}
