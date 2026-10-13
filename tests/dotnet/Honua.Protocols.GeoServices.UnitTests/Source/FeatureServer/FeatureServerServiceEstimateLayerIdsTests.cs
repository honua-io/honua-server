// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

/// <summary>
/// Service-level <c>getEstimates</c> reports each layer under its service-local index and
/// reads it from the publication's own storage-layer handle (SEC-4). The fixture publishes
/// <c>resource.parcels</c> as layer <see cref="ParcelsLayerIndex"/> while its storage handle
/// is <see cref="ParcelsStorageLayerId"/>, and <c>resource.permits</c> owns
/// <see cref="ParcelsLayerIndex"/> as its storage handle.
/// </summary>
public sealed class FeatureServerServiceEstimateLayerIdsTests
{
    private const int ParcelsLayerIndex = 3;
    private const int ParcelsStorageLayerId = 7;

    [UnitTest]
    public void ResolveServiceEstimateLayerIds_LayerIndexDiffersFromStorageId_ReadsThePublicationsStorage()
    {
        var snapshot = Snapshot();

        var ids = FeatureServerEndpoints.ResolveServiceEstimateLayerIds(
            snapshot,
            snapshot.Index.PublicationsById["pub.parcels"],
            snapshot.Index.ResourcesById["resource.parcels"]);

        ids.Should().Be((ParcelsLayerIndex, ParcelsStorageLayerId));
    }

    [UnitTest]
    public void ResolveServiceEstimateLayerIds_UnboundLayerIndexIsAnotherResourcesStorageId_IsSkipped()
    {
        var snapshot = Snapshot();

        var ids = FeatureServerEndpoints.ResolveServiceEstimateLayerIds(
            snapshot,
            snapshot.Index.PublicationsById["pub.unbound"],
            snapshot.Index.ResourcesById["resource.unbound"]);

        ids.Should().BeNull();
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
                    Binding("storage.permits", "resource.permits", ParcelsLayerIndex),
                ],
                Services =
                [
                    new MetadataV2Service
                    {
                        Metadata = new MetadataV2ObjectMetadata { Id = "service.features", Name = "features" },
                        Protocols = [ServiceProtocols.FeatureServer],
                        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                    }
                ],
                Publications =
                [
                    Publication("pub.parcels", "resource.parcels", "storage.parcels", ParcelsLayerIndex),
                    Publication("pub.unbound", "resource.unbound", storageBindingId: null, ParcelsLayerIndex),
                ],
            },
            "\"estimates\"",
            DateTimeOffset.UtcNow);

    private static MetadataV2Resource FeatureResource(string id, string? storageBindingId)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Type = MetadataV2ResourceType.FeatureDataset,
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            StorageBindingIds = storageBindingId is null ? [] : [storageBindingId],
            PrimaryStorageBindingId = storageBindingId,
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

    private static MetadataV2Publication Publication(
        string id,
        string resourceId,
        string? storageBindingId,
        int layerIndex)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
            Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
            ResourceId = resourceId,
            ServiceId = "service.features",
            StorageBindingId = storageBindingId,
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer,
            IsPrimary = true,
            LayerIndex = layerIndex,
        };
}
