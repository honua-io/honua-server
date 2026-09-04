// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;

namespace Honua.Core.Tests.Features.Capabilities;

public sealed class AdoptedLifecycleDefaultsTests
{
    private static readonly CapabilityRegistry Registry = new();

    public static TheoryData<string, CapabilityMaturity> AdoptedDescriptors => new()
    {
        { "serve.3d-tiles-scene", CapabilityMaturity.Experimental },
        { "serve.i3s-scene", CapabilityMaturity.Experimental },
        { "scene.catalog", CapabilityMaturity.Experimental },
        { "scene.bim-ingest", CapabilityMaturity.Experimental },
        { "scene.pointcloud-ingest", CapabilityMaturity.Experimental },
        { "alerts.geofence", CapabilityMaturity.Preview },
        { "sync.offline", CapabilityMaturity.Preview },
        { "admin.multi-tenancy", CapabilityMaturity.Preview },
    };

    [Theory]
    [MemberData(nameof(AdoptedDescriptors))]
    public void AdoptedDescriptor_HasLifecycleAndFourteenStateDenominator(
        string capabilityId,
        CapabilityMaturity expectedMaturity)
    {
        CapabilityDescriptor? descriptor = Registry.Find(capabilityId);
        descriptor.Should().NotBeNull();
        CapabilityDescriptor requiredDescriptor = descriptor!;
        requiredDescriptor.Maturity.Should().Be(expectedMaturity);

        CapabilityResolution disabled = Registry.Resolve(capabilityId, CapabilityGateContext.Default);
        disabled.Enabled.Should().BeFalse("every adopted lifecycle row is default-off");
        disabled.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);

        var enabledFlags = new CapabilityFlagOptions();
        enabledFlags.Capabilities[capabilityId] = new ExperimentalCapabilityFlag { Enabled = true };
        CapabilityResolution enabled = Registry.Resolve(capabilityId, new CapabilityGateContext
        {
            Edition = HonuaEdition.Enterprise,
            ExperimentalFlags = enabledFlags,
        });
        enabled.Enabled.Should().BeTrue("each row has an independent canonical opt-in");
        enabled.ReasonCode.Should().BeNull();
    }
}
