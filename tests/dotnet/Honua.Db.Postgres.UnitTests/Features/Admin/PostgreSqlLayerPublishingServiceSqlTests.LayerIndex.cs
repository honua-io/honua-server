// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using FluentAssertions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Admin;
using Honua.TestKit.Attributes;

namespace Honua.Db.Postgres.Tests.Features.Admin;

public sealed partial class PostgreSqlLayerPublishingServiceSqlTests
{
    private static readonly DateTimeOffset _layerIndexTime =
        DateTimeOffset.Parse("2026-11-21T18:00:00Z", CultureInfo.InvariantCulture);

    [UnitTest]
    public void ResolveServiceLayerIndexForPublish_EmptyService_ReturnsZero()
    {
        var index = PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            new MetadataV2Graph(),
            Allocation("svc-a", null, "res-1"));

        index.Should().Be(0);
    }

    [UnitTest]
    public void ResolveServiceLayerIndexForPublish_ExistingLayers_ReturnsMaxPlusOne()
    {
        var graph = new MetadataV2Graph
        {
            Publications =
            [
                NumericFeaturePublication("svc-a", "res-1", 0),
                NumericFeaturePublication("svc-a", "res-2", 3),
                NumericFeaturePublication("svc-b", "res-3", 9),
            ],
        };

        PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-a", null, "res-4")).Should().Be(4);
        PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-c", null, "res-4")).Should().Be(0);
    }

    [UnitTest]
    public void ResolveServiceLayerIndexForPublish_RequestedIndex_PreservesSourceIdAndRejectsCollision()
    {
        var graph = new MetadataV2Graph
        {
            Publications = [NumericFeaturePublication("svc-a", "res-1", 3)],
        };

        PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-a", 7, "res-2")).Should().Be(7);
        PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-a", 3, "res-1")).Should().Be(3);

        var collision = () => PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-a", 3, "res-2"));
        collision.Should().Throw<LayerPublishingException>()
            .Where(ex => ex.ErrorKind == LayerPublishingErrorKind.Conflict && ex.LayerId == null);

        var negative = () => PostgreSqlLayerPublishingService.ResolveServiceLayerIndexForPublish(
            graph, Allocation("svc-a", -1, "res-2"));
        negative.Should().Throw<LayerPublishingException>()
            .Where(ex => ex.ErrorKind == LayerPublishingErrorKind.Validation);
    }

    [UnitTest]
    public void BuildLinkedLayerMetadataV2Graph_NewService_AssignsZeroThenOne()
    {
        var graph = CreateTwoStorageLayerGraph();

        var first = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            graph, "beta", 10, "parcels", 4326, _layerIndexTime);
        first.Publications.Single(p => p.ResourceId == "res-10").LayerIndex.Should().Be(0);

        var second = PostgreSqlLayerPublishingService.BuildLinkedLayerMetadataV2Graph(
            first, "beta", 11, "roads", 4326, _layerIndexTime);
        second.Publications.Single(p => p.ResourceId == "res-11").LayerIndex.Should().Be(1);
    }

    private static PostgreSqlLayerPublishingService.ServiceLayerIndexAllocationRequest Allocation(
        string serviceId, int? requestedIndex, string resourceId)
        => new()
        {
            ServiceId = serviceId,
            ServiceName = serviceId,
            RequestedIndex = requestedIndex,
            ResourceId = resourceId,
            BindingId = $"storage-{resourceId}",
        };

    private static MetadataV2Layer NumericFeaturePublication(string serviceId, string resourceId, int index)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = $"pub-{serviceId}-{index}", Name = index.ToString(CultureInfo.InvariantCulture) },
            ServiceId = serviceId,
            ResourceId = resourceId,
            StorageBindingId = $"storage-{resourceId}",
            PublicationType = MetadataV2LayerType.EsriFeatureLayer,
            Identifier = new MetadataV2LayerIdentifier { Value = index.ToString(CultureInfo.InvariantCulture), IsNumeric = true },
        };

    private static MetadataV2Graph CreateTwoStorageLayerGraph()
        => new()
        {
            Revision = 1,
            Resources =
            [
                new MetadataV2Object { Metadata = new() { Id = "res-10", Name = "parcels" }, Type = MetadataV2ObjectType.FeatureDataset, StorageBindingIds = ["storage-layer-10"], PrimaryStorageBindingId = "storage-layer-10" },
                new MetadataV2Object { Metadata = new() { Id = "res-11", Name = "roads" }, Type = MetadataV2ObjectType.FeatureDataset, StorageBindingIds = ["storage-layer-11"], PrimaryStorageBindingId = "storage-layer-11" },
            ],
            StorageBindings =
            [
                new MetadataV2StorageBinding { Metadata = new() { Id = "storage-layer-10", Name = "storage-layer-10" }, ResourceId = "res-10", StorageType = MetadataV2StorageType.RelationalTable, Locator = "public.parcels", StorageLayerId = 10 },
                new MetadataV2StorageBinding { Metadata = new() { Id = "storage-layer-11", Name = "storage-layer-11" }, ResourceId = "res-11", StorageType = MetadataV2StorageType.RelationalTable, Locator = "public.roads", StorageLayerId = 11 },
            ],
        };
}
