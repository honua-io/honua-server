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
    public void Resolve_WithTenantFilter_ResolvesSingleUnambiguousMatchingTenantPublication()
    {
        var snapshot = CreateTwoTenantSnapshot(layerIndexA: 5, layerIndexB: 6);

        CogPublicationBinding.Resolve(snapshot, 5, "tenant-a")!.Metadata.Id.Should().Be("pub-a");
        CogPublicationBinding.Resolve(snapshot, 5, "tenant-b").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, tenantId: null).Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 6, "tenant-b")!.Metadata.Id.Should().Be("pub-b");
        CogPublicationBinding.Resolve(snapshot, 6, "tenant-a").Should().BeNull();
    }

    [UnitTest]
    public void Resolve_WithCrossTenantLayerIndexCollision_FailsClosedForAllTenants()
    {
        var snapshot = CreateTwoTenantSnapshot(layerIndexA: 5, layerIndexB: 5);

        CogPublicationBinding.Resolve(snapshot, 5, "tenant-a").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, "tenant-b").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, tenantId: null).Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5).Should().BeNull();
    }

    private static MetadataV2GraphSnapshot CreateTwoTenantSnapshot(int layerIndexA, int layerIndexB)
    {
        var graph = new MetadataV2Graph
        {
            Resources = [CreateTenantResource("a"), CreateTenantResource("b")],
            Services = [CreateTenantService("a"), CreateTenantService("b")],
            Publications = [CreateTenantPublication("a", layerIndexA), CreateTenantPublication("b", layerIndexB)]
        };
        return new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);
    }

    private static MetadataV2Object CreateTenantResource(string key) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = $"res-{key}", Name = key, Tenant = $"tenant-{key}" },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        Type = MetadataV2ObjectType.RasterDataset
    };

    private static MetadataV2Service CreateTenantService(string key) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = $"svc-{key}", Name = $"svc-{key}", Tenant = $"tenant-{key}" },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active }
    };

    private static MetadataV2Layer CreateTenantPublication(string key, int layerIndex) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = $"pub-{key}", Tenant = $"tenant-{key}" },
        Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
        ServiceId = $"svc-{key}",
        ResourceId = $"res-{key}",
        LayerIndex = layerIndex,
        PublicationType = MetadataV2LayerType.EsriImageLayer
    };
}
