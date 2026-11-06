// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerPublicationTenantTests
{
    [UnitTheory]
    [InlineData("tenant-a", 1)]
    [InlineData("tenant-b", 0)]
    [InlineData(null, 0)]
    public void GetRoutableFeaturePublicationsV2_PublicationTenant_RequiresMatchingRequestTenant(string? requestTenant, int expectedCount)
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(requestTenant);
        using var services = new ServiceCollection().AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        var service = new MetadataV2Service { Metadata = new MetadataV2ObjectMetadata { Id = "service", Name = "shared" } };
        var resource = new MetadataV2Resource { Metadata = new MetadataV2ObjectMetadata { Id = "resource", Name = "shared" } };
        var publication = new MetadataV2Publication
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "publication", Tenant = "tenant-a" },
            ServiceId = service.Metadata.Id,
            ResourceId = resource.Metadata.Id,
            LayerIndex = 0,
            PublicationType = MetadataV2PublicationType.EsriFeatureLayer
        };
        var snapshot = new MetadataV2GraphSnapshot(new MetadataV2Graph
        {
            Services = [service], Resources = [resource], Publications = [publication]
        }, "test", DateTimeOffset.UtcNow);

        var visible = FeatureServerEndpoints.GetRoutableFeaturePublicationsV2(service, snapshot, context);

        Assert.Equal(expectedCount, visible.Length);
    }
}
