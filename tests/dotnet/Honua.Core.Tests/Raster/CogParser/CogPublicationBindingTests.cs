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
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var graph = new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-a", Name = "a", Tenant = "tenant-a" },
                    Status = active,
                    Type = MetadataV2ObjectType.RasterDataset
                },
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-b", Name = "b", Tenant = "tenant-b" },
                    Status = active,
                    Type = MetadataV2ObjectType.RasterDataset
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-a", Name = "svc-a", Tenant = "tenant-a" },
                    Status = active
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-b", Name = "svc-b", Tenant = "tenant-b" },
                    Status = active
                }
            ],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-a", Tenant = "tenant-a" },
                    Status = active,
                    ServiceId = "svc-a",
                    ResourceId = "res-a",
                    LayerIndex = 5,
                    PublicationType = MetadataV2LayerType.EsriImageLayer
                },
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-b", Tenant = "tenant-b" },
                    Status = active,
                    ServiceId = "svc-b",
                    ResourceId = "res-b",
                    LayerIndex = 6,
                    PublicationType = MetadataV2LayerType.EsriImageLayer
                }
            ]
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);

        CogPublicationBinding.Resolve(snapshot, 5, "tenant-a")!.Metadata.Id.Should().Be("pub-a");
        CogPublicationBinding.Resolve(snapshot, 5, "tenant-b").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, tenantId: null).Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 6, "tenant-b")!.Metadata.Id.Should().Be("pub-b");
        CogPublicationBinding.Resolve(snapshot, 6, "tenant-a").Should().BeNull();
    }

    [UnitTest]
    public void Resolve_WithCrossTenantLayerIndexCollision_FailsClosedForAllTenants()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var graph = new MetadataV2Graph
        {
            Resources =
            [
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-a", Name = "a", Tenant = "tenant-a" },
                    Status = active,
                    Type = MetadataV2ObjectType.RasterDataset
                },
                new MetadataV2Object
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "res-b", Name = "b", Tenant = "tenant-b" },
                    Status = active,
                    Type = MetadataV2ObjectType.RasterDataset
                }
            ],
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-a", Name = "svc-a", Tenant = "tenant-a" },
                    Status = active
                },
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "svc-b", Name = "svc-b", Tenant = "tenant-b" },
                    Status = active
                }
            ],
            Publications =
            [
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-a", Tenant = "tenant-a" },
                    Status = active,
                    ServiceId = "svc-a",
                    ResourceId = "res-a",
                    LayerIndex = 5,
                    PublicationType = MetadataV2LayerType.EsriImageLayer
                },
                new MetadataV2Layer
                {
                    Metadata = new MetadataV2ObjectMetadata { Id = "pub-b", Tenant = "tenant-b" },
                    Status = active,
                    ServiceId = "svc-b",
                    ResourceId = "res-b",
                    LayerIndex = 5,
                    PublicationType = MetadataV2LayerType.EsriImageLayer
                }
            ]
        };
        var snapshot = new MetadataV2GraphSnapshot(graph, "test", DateTimeOffset.UtcNow);

        CogPublicationBinding.Resolve(snapshot, 5, "tenant-a").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, "tenant-b").Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5, tenantId: null).Should().BeNull();
        CogPublicationBinding.Resolve(snapshot, 5).Should().BeNull();
    }
}
