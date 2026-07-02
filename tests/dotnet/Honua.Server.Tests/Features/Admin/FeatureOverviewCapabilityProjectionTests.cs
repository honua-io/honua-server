// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Server.Features.Admin;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Unit coverage for the admin feature-overview capability projection (Track T8 /
/// #2344): the read-only surface that projects the unified capability registry
/// (ADR-0058, Decision B) through the T2 gate resolver so operators can see the
/// full roster and <b>why</b> each capability is on or off.
/// </summary>
public sealed class FeatureOverviewCapabilityProjectionTests
{
    private static readonly CapabilityDescriptor ImplementedDescriptor = new()
    {
        Id = "test.implemented.capability",
        Category = "test",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Implemented,
    };

    private static readonly CapabilityDescriptor ExperimentalDescriptor = new()
    {
        Id = "test.experimental.capability",
        Category = "test",
        Kind = CapabilityKind.ProtocolOperation,
        Maturity = CapabilityMaturity.Experimental,
    };

    [Fact]
    public void ProjectCapabilities_CarriesIdKindMaturityAndResolvedState()
    {
        var items = FeatureOverviewEndpoints.ProjectCapabilities(
            [ImplementedDescriptor],
            CapabilityGateContext.Default);

        var item = items.Should().ContainSingle().Subject;
        item.Id.Should().Be("test.implemented.capability");
        item.Kind.Should().Be(nameof(CapabilityKind.Feature));
        item.Maturity.Should().Be(nameof(CapabilityMaturity.Implemented));
        item.Enabled.Should().BeTrue();
        item.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void ProjectCapabilities_ExperimentalWithFlagsOff_ShowsDisabledWithReasonCode()
    {
        // Default context leaves the experimental flags off, so an experimental
        // capability resolves disabled with the experimental-disabled reason code.
        var items = FeatureOverviewEndpoints.ProjectCapabilities(
            [ExperimentalDescriptor],
            CapabilityGateContext.Default);

        var item = items.Should().ContainSingle().Subject;
        item.Id.Should().Be("test.experimental.capability");
        item.Maturity.Should().Be(nameof(CapabilityMaturity.Experimental));
        item.Enabled.Should().BeFalse();
        item.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void ProjectCapabilities_ExperimentalWithFlagOn_ShowsEnabled()
    {
        var flags = new CapabilityFlagOptions { Enabled = true };
        var context = new CapabilityGateContext { ExperimentalFlags = flags };

        var items = FeatureOverviewEndpoints.ProjectCapabilities([ExperimentalDescriptor], context);

        var item = items.Should().ContainSingle().Subject;
        item.Enabled.Should().BeTrue();
        item.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void ProjectCapabilities_PreservesRegistryOrder()
    {
        var items = FeatureOverviewEndpoints.ProjectCapabilities(
            [ImplementedDescriptor, ExperimentalDescriptor],
            CapabilityGateContext.Default);

        items.Should().HaveCount(2);
        items[0].Id.Should().Be(ImplementedDescriptor.Id);
        items[1].Id.Should().Be(ExperimentalDescriptor.Id);
    }
}
