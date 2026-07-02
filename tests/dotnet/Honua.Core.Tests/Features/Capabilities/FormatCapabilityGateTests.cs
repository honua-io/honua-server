// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using FluentAssertions;
using Honua.Core.Features.Capabilities;

namespace Honua.Core.Tests.Features.Capabilities;

/// <summary>
/// Unit coverage for the shared format-negotiation gate (#2342 / T6): mapping a
/// data-format name to its <c>format.*</c> descriptor, consulting the T2 resolver,
/// and translating the outcome into a format-scoped <see cref="FormatGateDecision"/>.
/// Enabled formats stay enabled; a disabled-experimental format and an unknown
/// format resolve to a non-enabled decision so a seam returns a 400 rather than
/// silently serving or coercing the format.
/// </summary>
public sealed class FormatCapabilityGateTests
{
    // A registry that reports a single format id as experimental, so the
    // experimental-disabled path is exercised without flipping the real (all-Implemented)
    // roster — that flip is T10 (#2346). Every other id resolves via the real
    // CapabilityGateResolver precedence against the supplied descriptor.
    private sealed class ExperimentalFormatRegistry : ICapabilityRegistry
    {
        private readonly string _experimentalId;

        public ExperimentalFormatRegistry(string experimentalId) => _experimentalId = experimentalId;

        public IReadOnlyList<CapabilityDescriptor> All => [];

        public CapabilityDescriptor? Find(string id) => string.Equals(id, _experimentalId, System.StringComparison.Ordinal)
            ? new CapabilityDescriptor
            {
                Id = _experimentalId,
                Category = "format",
                Kind = CapabilityKind.DataFormat,
                Maturity = CapabilityMaturity.Experimental,
            }
            : null;

        public CapabilityResolution Resolve(string id, CapabilityGateContext context)
            => CapabilityGateResolver.Resolve(Find(id), context);
    }

    [Fact]
    public void Evaluate_RegisteredImplementedFormat_IsEnabled()
    {
        // geojson is a real, Implemented format.* descriptor in the default registry.
        var decision = FormatCapabilityGate.Evaluate("geojson");

        decision.IsEnabled.Should().BeTrue();
        decision.IsBlocked.Should().BeFalse();
        decision.Status.Should().Be(FormatGateStatus.Enabled);
        decision.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void Evaluate_IsCaseInsensitiveOnFormatName()
    {
        FormatCapabilityGate.Evaluate("GeoParquet").IsEnabled.Should().BeTrue();
        FormatCapabilityGate.Evaluate("ESRIJSON").IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_UnknownFormat_IsUnknown_AndNotBlocked()
    {
        // A wire token with no format.* descriptor (for example the GeoServices
        // f=pbf / f=arrow output tokens) is not registry-managed: Unknown, not blocked.
        var decision = FormatCapabilityGate.Evaluate("pbf");

        decision.IsEnabled.Should().BeFalse();
        decision.IsBlocked.Should().BeFalse();
        decision.Status.Should().Be(FormatGateStatus.Unknown);
        decision.ReasonCode.Should().Be(FormatCapabilityReasonCodes.Unknown);
    }

    [Fact]
    public void Evaluate_ExperimentalFormatWithFlagsOff_IsExperimentalDisabled_AndBlocked()
    {
        var registry = new ExperimentalFormatRegistry(CapabilityRegistry.DataFormatIdPrefix + "geoparquet");

        var decision = FormatCapabilityGate.Evaluate("geoparquet", context: null, registry: registry);

        decision.IsEnabled.Should().BeFalse();
        decision.IsBlocked.Should().BeTrue();
        decision.Status.Should().Be(FormatGateStatus.ExperimentalDisabled);
        decision.ReasonCode.Should().Be(FormatCapabilityReasonCodes.ExperimentalDisabled);
    }

    [Fact]
    public void Evaluate_ExperimentalFormatWithGlobalFlagOn_IsEnabled()
    {
        var registry = new ExperimentalFormatRegistry(CapabilityRegistry.DataFormatIdPrefix + "geoparquet");
        var context = new CapabilityGateContext
        {
            ExperimentalFlags = new CapabilityFlagOptions { Enabled = true },
        };

        var decision = FormatCapabilityGate.Evaluate("geoparquet", context, registry);

        decision.IsEnabled.Should().BeTrue();
        decision.IsBlocked.Should().BeFalse();
        decision.ReasonCode.Should().BeNull();
    }

    [Fact]
    public void Evaluate_ExperimentalFormatWithPerCapabilityFlagOn_IsEnabled()
    {
        var id = CapabilityRegistry.DataFormatIdPrefix + "geoparquet";
        var registry = new ExperimentalFormatRegistry(id);
        var flags = new CapabilityFlagOptions();
        flags.Capabilities[id] = new ExperimentalCapabilityFlag { Enabled = true };
        var context = new CapabilityGateContext { ExperimentalFlags = flags };

        FormatCapabilityGate.Evaluate("geoparquet", context, registry).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NullOrWhitespaceFormat_Throws()
    {
        var evaluateNull = () => FormatCapabilityGate.Evaluate(null!);
        var evaluateBlank = () => FormatCapabilityGate.Evaluate("   ");

        evaluateNull.Should().Throw<System.ArgumentException>();
        evaluateBlank.Should().Throw<System.ArgumentException>();
    }

    [Fact]
    public void FilterAdvertised_KeepsEnabledAndUnknown_DropsBlocked()
    {
        var id = CapabilityRegistry.DataFormatIdPrefix + "geoparquet";
        var registry = new ExperimentalFormatRegistry(id);

        // geoparquet is experimental-disabled in this registry; geojson resolves via the
        // registry (unknown here -> kept); pbf is not registry-managed (kept). Only the
        // blocked geoparquet is dropped.
        var kept = FormatCapabilityGate.FilterAdvertised(
            ["geojson", "geoparquet", "pbf"],
            context: null,
            registry: registry);

        kept.Should().Equal("geojson", "pbf");
    }

    [Fact]
    public void FilterAdvertised_DefaultRegistry_KeepsAllRealFormats()
    {
        // Every format is Implemented in T6, so nothing is filtered from the real roster.
        var kept = FormatCapabilityGate.FilterAdvertised(["geojson", "geoparquet", "esrijson", "flatgeobuf"]);

        kept.Should().Equal("geojson", "geoparquet", "esrijson", "flatgeobuf");
    }
}
