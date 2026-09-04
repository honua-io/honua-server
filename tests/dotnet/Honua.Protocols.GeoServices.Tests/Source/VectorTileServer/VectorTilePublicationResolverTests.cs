// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.GeoServices.VectorTileServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.VectorTileServer;

[Protocol(TestProtocols.VectorTileServer)]
public sealed class VectorTilePublicationResolverTests
{
    [UnitTest]
    public void ResolvePrimary_MultiPublicationService_UsesSameDeclaredPrimaryRegardlessOfGraphOrder()
    {
        var active = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active };
        var service = new MetadataV2Service
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "service-vt", Name = "multi" },
            Status = active,
            Protocols = [ServiceProtocols.VectorTileServer]
        };
        var firstResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-first", Name = "first" },
            Status = active
        };
        var primaryResource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "resource-primary", Name = "primary" },
            Status = active
        };
        var firstPublication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-first" },
            ServiceId = service.Metadata.Id,
            ResourceId = firstResource.Metadata.Id,
            LayerIndex = 0,
            PublicationType = MetadataV2PublicationType.EsriVectorTileLayer,
            Status = active
        };
        var primaryPublication = firstPublication with
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication-primary" },
            ResourceId = primaryResource.Metadata.Id,
            LayerIndex = 7,
            IsPrimary = true
        };

        var snapshot = new MetadataV2GraphSnapshot(
            new MetadataV2Graph
            {
                Services = [service],
                Resources = [firstResource, primaryResource],
                Publications = [primaryPublication, firstPublication]
            },
            "\"test\"",
            DateTimeOffset.UtcNow);

        var result = VectorTilePublicationResolver.ResolvePrimary(snapshot, service);

        result.Should().NotBeNull();
        result!.Value.Publication.Should().Be(primaryPublication);
        result.Value.Resource.Should().Be(primaryResource);
    }
}
