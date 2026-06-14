// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.Metadata.Domain.V2;

/// <summary>
/// Tests for the tenant-scope visibility rules that back cross-tenant isolation in
/// the OGC API and STAC metadata lookup helpers (#1580).
/// </summary>
[Protocol(ProtocolNames.TestQuality)]
public sealed class MetadataV2TenantVisibilityTests
{
    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_UnscopedEntity_VisibleToEveryRequest()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: null, requestTenant: "acme").Should().BeTrue();
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "   ", requestTenant: "acme").Should().BeTrue();
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: null, requestTenant: null).Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ScopedEntity_NoRequestTenant_FailsClosed()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "acme", requestTenant: null).Should().BeFalse();
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "acme", requestTenant: "  ").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ScopedEntity_MatchingTenant_Visible()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "acme", requestTenant: "acme").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ScopedEntity_DifferentTenant_Hidden()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "acme", requestTenant: "globex").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ScopedEntity_TenantComparisonIsCaseSensitive()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant(entityTenant: "Acme", requestTenant: "acme").Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_NullResourceOrService_TreatedAsVisible()
    {
        MetadataV2TenantVisibility.IsVisibleToTenant((MetadataV2Resource?)null, "acme").Should().BeTrue();
        MetadataV2TenantVisibility.IsVisibleToTenant((MetadataV2Service?)null, "acme").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ForeignTenantResource_Hidden()
    {
        var resource = CreateResource(tenant: "acme");

        MetadataV2TenantVisibility.IsVisibleToTenant(resource, "globex").Should().BeFalse();
        MetadataV2TenantVisibility.IsVisibleToTenant(resource, "acme").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_ForeignTenantService_Hidden()
    {
        var service = CreateService(tenant: "acme");

        MetadataV2TenantVisibility.IsVisibleToTenant(service, "globex").Should().BeFalse();
        MetadataV2TenantVisibility.IsVisibleToTenant(service, "acme").Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_AllNodesUnscoped_Visible()
    {
        var publication = CreatePublication(tenant: null);
        var resource = CreateResource(tenant: null);
        var service = CreateService(tenant: null);

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, "globex")
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_AllNodesMatchTenant_Visible()
    {
        var publication = CreatePublication(tenant: "acme");
        var resource = CreateResource(tenant: "acme");
        var service = CreateService(tenant: "acme");

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, "acme")
            .Should().BeTrue();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_ForeignPublication_Hidden()
    {
        var publication = CreatePublication(tenant: "acme");
        var resource = CreateResource(tenant: null);
        var service = CreateService(tenant: null);

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, "globex")
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_ForeignResource_Hidden()
    {
        var publication = CreatePublication(tenant: null);
        var resource = CreateResource(tenant: "acme");
        var service = CreateService(tenant: null);

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, "globex")
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_ForeignService_Hidden()
    {
        var publication = CreatePublication(tenant: null);
        var resource = CreateResource(tenant: null);
        var service = CreateService(tenant: "acme");

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, "globex")
            .Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Metadata)]
    public void IsVisibleToTenant_PublishedSurface_ScopedButNoRequestTenant_Hidden()
    {
        var publication = CreatePublication(tenant: "acme");
        var resource = CreateResource(tenant: "acme");
        var service = CreateService(tenant: "acme");

        MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, requestTenant: null)
            .Should().BeFalse();
    }

    private static MetadataV2Resource CreateResource(string? tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata
        {
            Id = "parcels",
            Name = "parcels",
            Tenant = tenant,
        },
        Type = MetadataV2ResourceType.FeatureDataset,
    };

    private static MetadataV2Service CreateService(string? tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata
        {
            Id = "service.parcels",
            Name = "service.parcels",
            Tenant = tenant,
        },
        ServiceType = MetadataV2ServiceType.OgcApiFeatures,
    };

    private static MetadataV2Publication CreatePublication(string? tenant) => new()
    {
        Metadata = new MetadataV2ObjectMetadata
        {
            Id = "pub.parcels",
            Name = "pub.parcels",
            Tenant = tenant,
        },
        ServiceId = "service.parcels",
        ResourceId = "parcels",
        PublicationType = MetadataV2PublicationType.OgcCollection,
    };
}
