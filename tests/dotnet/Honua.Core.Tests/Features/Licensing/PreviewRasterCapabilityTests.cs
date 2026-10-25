// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Licensing;

public class PreviewRasterCapabilityTests
{
    [Theory]
    [Trait("Tier", "Fast")]
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

    [Fact]
    [Trait("Tier", "Fast")]
    public void ImageServer_IsGaInTwentyTwentySixOne_WithUnchangedDefaultAvailability()
    {
        // 2026-10-24 operator ruling: ImageServer left Preview; it still serves by default
        // with no opt-in, and the experimental flag is not needed to resolve it.
        const string capabilityId = "serve.geoservices-imageserver";
        var registry = new CapabilityRegistry();
        var descriptor = registry.Find(capabilityId);
        Assert.NotNull(descriptor);
        Assert.Equal(CapabilityMaturity.Implemented, descriptor.Maturity);
        Assert.NotEqual(CapabilityKeyCatalog.PreviewStatus,
            CapabilityKeyCatalog.All.Single(capability => capability.Key == capabilityId).Status);
        Assert.False(descriptor.RequiresOptIn);
        Assert.True(registry.Resolve(capabilityId, CapabilityGateContext.Default).Enabled);
    }
}
