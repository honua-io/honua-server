// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Core.Tests.Features.Capabilities;

/// <summary>
/// Unit coverage for the config-driven experimental feature-flag resolver
/// (#2339 / T2): the maturity/flag/edition precedence in
/// <see cref="CapabilityGateResolver"/>, plus the <see cref="CapabilityFlagOptions"/>
/// config binding for the <c>Capabilities:Experimental</c> schema.
/// </summary>
public sealed class CapabilityGateResolverTests
{
    // A still-experimental capability id for the synthetic resolver descriptors below.
    // (temporal.* was promoted to GA in #2429, so it is no longer a valid experimental
    // example; sync.offline remains built-experimental and gated off by default.)
    private const string ExperimentalId = "sync.offline";

    private static CapabilityDescriptor Experimental(HonuaEdition? minimumEdition = null) => new()
    {
        Id = ExperimentalId,
        Category = "sync",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Experimental,
        MinimumEdition = minimumEdition,
    };

    private static CapabilityDescriptor NonExperimental(HonuaEdition? minimumEdition = null) => new()
    {
        Id = "query.features",
        Category = "query",
        Kind = CapabilityKind.Feature,
        Maturity = CapabilityMaturity.Implemented,
        MinimumEdition = minimumEdition,
    };

    private static CapabilityGateContext Context(
        CapabilityFlagOptions? flags = null,
        HonuaEdition edition = HonuaEdition.Community) => new()
        {
            Edition = edition,
            ExperimentalFlags = flags ?? new CapabilityFlagOptions(),
        };

    private static CapabilityFlagOptions GlobalOn() => new() { Enabled = true };

    private static CapabilityFlagOptions PerCapabilityOn(string id, bool enabled = true)
    {
        var options = new CapabilityFlagOptions();
        options.Capabilities[id] = new ExperimentalCapabilityFlag { Enabled = enabled };
        return options;
    }

    [Fact]
    public void Experimental_WithFlagsOff_IsDisabledWithExperimentalReason()
    {
        var resolution = CapabilityGateResolver.Resolve(Experimental(), Context());

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void Experimental_WithGlobalFlagOn_IsEnabled()
    {
        var resolution = CapabilityGateResolver.Resolve(Experimental(), Context(GlobalOn()));

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void Experimental_WithPerCapabilityFlagOn_IsEnabled()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(),
            Context(PerCapabilityOn(ExperimentalId)));

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void Experimental_WithPerCapabilityFlagExplicitlyOff_IsDisabled()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(),
            Context(PerCapabilityOn(ExperimentalId, enabled: false)));

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void Experimental_WithPerCapabilityFlagForDifferentId_IsDisabled()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(),
            Context(PerCapabilityOn("some.other.capability")));

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void Experimental_WithGlobalFlagOn_ButWrongEdition_FailsOnEditionNotFlag()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(HonuaEdition.Enterprise),
            Context(GlobalOn(), edition: HonuaEdition.Community));

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.LicenseRequired);
        resolution.ReasonCode.Should().NotBe(CapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void Experimental_WithPerCapabilityFlagOn_ButWrongEdition_FailsOnEdition()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(HonuaEdition.Pro),
            Context(PerCapabilityOn(ExperimentalId), edition: HonuaEdition.Community));

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.LicenseRequired);
    }

    [Fact]
    public void Experimental_WithFlagOn_AndSatisfiedEdition_IsEnabled()
    {
        var resolution = CapabilityGateResolver.Resolve(
            Experimental(HonuaEdition.Pro),
            Context(GlobalOn(), edition: HonuaEdition.Enterprise));

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void NonExperimental_IsEnabled_EvenWithFlagsOff()
    {
        var resolution = CapabilityGateResolver.Resolve(NonExperimental(), Context());

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void NonExperimental_IsEnabled_EvenWhenEditionBelowMinimum()
    {
        // The registry description layer does not entitlement-gate non-experimental
        // capabilities (B1 pass-through); operation surfaces enforce edition/entitlement.
        var resolution = CapabilityGateResolver.Resolve(
            NonExperimental(HonuaEdition.Enterprise),
            Context(edition: HonuaEdition.Community));

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void UnknownDescriptor_IsNotRegistered()
    {
        var resolution = CapabilityGateResolver.Resolve(null, Context());

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityReasonCodes.NotRegistered);
    }

    [Fact]
    public void Registry_UnknownId_IsNotRegistered()
    {
        var registry = new CapabilityRegistry();

        var resolution = registry.Resolve("does.not.exist", CapabilityGateContext.Default);

        resolution.Enabled.Should().BeFalse();
        resolution.ReasonCode.Should().Be(CapabilityRegistry.NotRegisteredReasonCode);
    }

    [Fact]
    public void Registry_RegisteredImplementedCapability_ResolvesEnabledUnderDefaultContext()
    {
        var registry = new CapabilityRegistry();
        var descriptor = registry.All[0];

        var resolution = registry.Resolve(descriptor.Id, CapabilityGateContext.Default);

        resolution.Enabled.Should().BeTrue();
        resolution.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void IsExperimentalEnabled_GlobalSwitch_OverridesMissingPerCapability()
    {
        var options = new CapabilityFlagOptions { Enabled = true };

        options.IsExperimentalEnabled("anything.at.all").Should().BeTrue();
    }

    [Fact]
    public void IsExperimentalEnabled_DefaultOptions_AreAllOff()
    {
        var options = new CapabilityFlagOptions();

        options.Enabled.Should().BeFalse();
        options.IsExperimentalEnabled(ExperimentalId).Should().BeFalse();
    }

    [Fact]
    public void Bind_ReadsGlobalSwitchAndPerCapabilityOverrides()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capabilities:Experimental:Enabled"] = "true",
                ["Capabilities:Experimental:temporal.filtering:Enabled"] = "true",
                ["Capabilities:Experimental:format.geoparquet:Enabled"] = "false",
            })
            .Build();

        var options = new CapabilityFlagOptions();
        CapabilityFlagOptions.Bind(options, configuration.GetSection(CapabilityFlagOptions.SectionName));

        options.Enabled.Should().BeTrue();
        options.Capabilities.Should().ContainKey("temporal.filtering");
        options.Capabilities["temporal.filtering"].Enabled.Should().BeTrue();
        options.Capabilities.Should().ContainKey("format.geoparquet");
        options.Capabilities["format.geoparquet"].Enabled.Should().BeFalse();
        // The global "Enabled" scalar is not misread as a per-capability entry.
        options.Capabilities.Should().NotContainKey("Enabled");
    }

    [Fact]
    public void Bind_DefaultsToAllOff_WhenSectionAbsent()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = new CapabilityFlagOptions();
        CapabilityFlagOptions.Bind(options, configuration.GetSection(CapabilityFlagOptions.SectionName));

        options.Enabled.Should().BeFalse();
        options.Capabilities.Should().BeEmpty();
    }

    [Fact]
    public void AddCapabilityFlagOptions_BindsFromConfigurationIntoOptions()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Capabilities:Experimental:temporal.filtering:Enabled"] = "true",
            })
            .Build();

        using var provider = new ServiceCollection()
            .AddCapabilityFlagOptions(configuration)
            .BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<CapabilityFlagOptions>>().Value;

        options.Enabled.Should().BeFalse();
        options.IsExperimentalEnabled("temporal.filtering").Should().BeTrue();
        options.IsExperimentalEnabled("format.geoparquet").Should().BeFalse();
    }
}
