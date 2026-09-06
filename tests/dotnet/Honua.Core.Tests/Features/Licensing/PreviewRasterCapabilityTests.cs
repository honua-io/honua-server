// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Licensing;

public class PreviewRasterCapabilityTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData("serve.geoservices-imageserver")]
    [InlineData("serve.wmts")]
    [InlineData("serve.ogc-api-coverages")]
    [InlineData("serve.ogc-api-edr")]
    public void PreviewSurface_RetainsLifecycleAndExistingAvailability(string capabilityId)
    {
        var registry = new CapabilityRegistry();
        var descriptor = registry.Find(capabilityId);
        Assert.NotNull(descriptor);
        Assert.Equal(CapabilityMaturity.Preview, descriptor.Maturity);
        Assert.Equal(CapabilityKeyCatalog.PreviewStatus,
            CapabilityKeyCatalog.All.Single(capability => capability.Key == capabilityId).Status);
        var requiresOptIn = capabilityId == "serve.ogc-api-edr";
        Assert.Equal(requiresOptIn, descriptor.RequiresOptIn);
        Assert.Equal(!requiresOptIn, registry.Resolve(capabilityId, CapabilityGateContext.Default).Enabled);

        var flags = new CapabilityFlagOptions();
        flags.Capabilities[capabilityId] = new ExperimentalCapabilityFlag { Enabled = true };
        Assert.True(registry.Resolve(capabilityId, new CapabilityGateContext { ExperimentalFlags = flags }).Enabled);
        Assert.Equal(CapabilityMaturity.Preview, registry.Find(capabilityId)!.Maturity);
    }
}
