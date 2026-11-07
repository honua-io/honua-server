// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Hosting;
using Honua.Server.Features.Capabilities;
using Honua.Server.Features.Capabilities.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Catalog;

public sealed class CatalogManifestProtocolTests
{
    [UnitTest]
    public void Build_RestrictedDeploymentProfile_UsesRuntimeRouteCatalog()
    {
        var profile = new CapabilityManifestDeploymentProfile
        {
            Configured = true,
            SchemaVersion = "1.0.0",
            EnabledCapabilities = ["serve.wms"]
        };
        var routes = new ManifestRoutedCapabilityIndex(new DeploymentCapabilityRouteCatalog());

        var endpoints = CapabilityManifestProtocolEndpoints.Build([Capability("serve.wms")], profile, routes);

        if (ProtocolProfile.IncludesOgcClassic)
        {
            Assert.Equal(ServiceProtocols.Wms, Assert.Single(endpoints).Protocol);
        }
        else
        {
            Assert.Empty(endpoints);
        }
    }

    [UnitTest]
    public void Build_UnavailableGatedProtocols_OmitsTheirDescriptors()
    {
        var profile = new CapabilityManifestDeploymentProfile
        {
            Configured = false,
            SchemaVersion = null,
            EnabledCapabilities = []
        };
        var routes = new ManifestRoutedCapabilityIndex(new DeploymentCapabilityRouteCatalog());
        var unavailable = new[] { "serve.wmts", "serve.geoservices-imageserver" }
            .Select(id => new CapabilityManifestCapability
            {
                Id = id, Category = "serve", Lifecycle = "implemented", Supported = true,
                Available = false, OptInRequired = false, ReasonCode = "experimental-disabled"
            }).ToArray();

        var endpoints = CapabilityManifestProtocolEndpoints.Build(unavailable, profile, routes);

        Assert.DoesNotContain(endpoints, endpoint => endpoint.Protocol == ServiceProtocols.Wmts);
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Protocol == ServiceProtocols.ImageServer);
    }
    [UnitTest]
    public void Build_CallerUnavailableCapability_OmitsOnlyThatProtocol()
    {
        var profile = new CapabilityManifestDeploymentProfile { Configured = false, SchemaVersion = null, EnabledCapabilities = [] };
        var routes = new ManifestRoutedCapabilityIndex(new DeploymentCapabilityRouteCatalog());
        var endpoints = CapabilityManifestProtocolEndpoints.Build(
            [Capability("serve.ogc-api-features"), Capability("serve.wms") with { Available = false, ReasonCode = "caller-denied" }], profile, routes);
        if (ProtocolProfile.IncludesOgcApi)
        {
            Assert.Equal(ServiceProtocols.OgcFeatures, Assert.Single(endpoints).Protocol);
        }
        else
        {
            Assert.Empty(endpoints);
        }
        Assert.DoesNotContain(endpoints, endpoint => endpoint.Protocol == ServiceProtocols.Wms);
    }

    private static CapabilityManifestCapability Capability(string id) => new()
    {
        Id = id, Category = "serve", Lifecycle = "implemented", Supported = true,
        Available = true, OptInRequired = false, ReasonCode = null
    };

}
