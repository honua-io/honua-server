// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.Licensing.Domain;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

[Trait("Category", "Architecture")]
public sealed class CapabilityKeyStatusDriftTests
{
    private static readonly Dictionary<CapabilityMaturity, string> StatusByMaturity = new()
    {
        [CapabilityMaturity.Experimental] = CapabilityKeyCatalog.ExperimentalStatus,
        [CapabilityMaturity.Preview] = CapabilityKeyCatalog.PreviewStatus,
        [CapabilityMaturity.Internal] = CapabilityKeyCatalog.InternalStatus,
    };

    [ArchitectureTest]
    public void PreReleaseRegistryCapabilities_CarryTheirMaturityAsKeyCatalogStatus()
    {
        var statusByKey = CapabilityKeyCatalog.All.ToDictionary(
            static key => key.Key, static key => key.Status, StringComparer.Ordinal);

        var drifted = new CapabilityRegistry().All
            .Where(static descriptor => StatusByMaturity.ContainsKey(descriptor.Maturity))
            .SelectMany(descriptor => CatalogKeysOf(descriptor, statusByKey)
                .Where(key => statusByKey[key] != StatusByMaturity[descriptor.Maturity])
                .Select(key => $"{descriptor.Id} ({descriptor.Maturity}) -> {key} status '{statusByKey[key] ?? "ga"}'"))
            .ToArray();

        drifted.Should().BeEmpty(
            "a registry capability that is not GA must not read as GA in CapabilityKeyCatalog, "
            + "capability-keys.v1.json or the generated OKF pages agents read.");
    }

    [ArchitectureTest]
    public void PreReleaseRegistryCapabilities_EachResolveToACatalogKey()
    {
        var statusByKey = CapabilityKeyCatalog.All.ToDictionary(
            static key => key.Key, static key => key.Status, StringComparer.Ordinal);

        var unkeyed = new CapabilityRegistry().All
            .Where(static descriptor => StatusByMaturity.ContainsKey(descriptor.Maturity))
            .Where(descriptor => !CatalogKeysOf(descriptor, statusByKey).Any())
            .Select(static descriptor => descriptor.Id)
            .ToArray();

        unkeyed.Should().BeEmpty(
            "a pre-release capability with no catalog key escapes the status check above; "
            + "give it a key in CapabilityKeyCatalog or an entitlement key that has one.");
    }

    private static IEnumerable<string> CatalogKeysOf(
        CapabilityDescriptor descriptor,
        Dictionary<string, string?> statusByKey)
        => new[] { descriptor.Id, descriptor.EntitlementKey }
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Where(statusByKey.ContainsKey);
}
