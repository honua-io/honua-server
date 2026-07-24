// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Licensing.EntitlementSweep;

/// <summary>
/// Fail-closed coverage guard for the local entitlement sweep (#2980): every Pro/Enterprise
/// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog"/> key must resolve to
/// exactly one of <see cref="EntitlementProbeRegistry.Probes"/>,
/// <see cref="EntitlementProbeRegistry.NoHttpSurfaceAllowlist"/>, or
/// <see cref="EntitlementProbeRegistry.KnownGaps"/>. A newly added gated entitlement key ships
/// with no registry row here fails this test — the sweep cannot be silently bypassed by adding
/// a feature without also deciding how it is (or isn't) probed.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Operation(Operations.LicenseManagement)]
public sealed class EntitlementSweepCoverageTests
{
    [UnitTest]
    public void EveryGatedKey_HasExactlyOneRegistryRow()
    {
        var unmapped = new List<string>();
        var conflicting = new List<string>();

        foreach (var feature in EntitlementProbeRegistry.GatedKeys)
        {
            var buckets = 0;
            if (EntitlementProbeRegistry.Probes.ContainsKey(feature.Key))
            {
                buckets++;
            }

            if (EntitlementProbeRegistry.NoHttpSurfaceAllowlist.ContainsKey(feature.Key))
            {
                buckets++;
            }

            if (EntitlementProbeRegistry.KnownGaps.ContainsKey(feature.Key))
            {
                buckets++;
            }

            switch (buckets)
            {
                case 0:
                    unmapped.Add(feature.Key);
                    break;
                case > 1:
                    conflicting.Add(feature.Key);
                    break;
            }
        }

        unmapped.Should().BeEmpty(
            "every Pro/Enterprise FeatureCatalog key must have a probe, an allowlist reason, " +
            "or a documented known-gap entry in EntitlementProbeRegistry — a new gated key " +
            "shipped without one of these rows, which means it can ship unswept");
        conflicting.Should().BeEmpty(
            "a key must not appear in more than one of Probes/NoHttpSurfaceAllowlist/KnownGaps " +
            "— that is a contradictory classification");
    }

    [UnitTest]
    public void NoRegistryRow_ReferencesAnUnknownKey()
    {
        var gatedKeys = EntitlementProbeRegistry.GatedKeys.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);

        var strayProbes = EntitlementProbeRegistry.Probes.Keys.Where(k => !gatedKeys.Contains(k)).ToList();
        var strayAllowlist = EntitlementProbeRegistry.NoHttpSurfaceAllowlist.Keys
            .Where(k => !gatedKeys.Contains(k)).ToList();
        var strayGaps = EntitlementProbeRegistry.KnownGaps.Keys.Where(k => !gatedKeys.Contains(k)).ToList();

        strayProbes.Should().BeEmpty("a probe references a key that is not a gated FeatureCatalog entry (renamed or removed?)");
        strayAllowlist.Should().BeEmpty("an allowlist entry references a key that is not a gated FeatureCatalog entry (renamed or removed?)");
        strayGaps.Should().BeEmpty("a known-gap entry references a key that is not a gated FeatureCatalog entry (renamed or removed?)");
    }

    [UnitTest]
    public void KnownGaps_AreLoudAndMinimal()
    {
        foreach (var gap in EntitlementProbeRegistry.KnownGaps.Values)
        {
            gap.Reason.Should().NotBeNullOrWhiteSpace($"known gap '{gap.Key}' must document why it is unenforced");
            gap.TrackingIssue.Should().NotBeNullOrWhiteSpace($"known gap '{gap.Key}' must reference a tracking issue");
        }

        // Loud: known gaps must be a small, deliberate minority of the gated surface, never the
        // majority — if this starts creeping up, the sweep is being used to paper over real
        // enforcement debt instead of tracking a handful of pending decisions.
        var gatedCount = EntitlementProbeRegistry.GatedKeys.Count;
        var gapCount = EntitlementProbeRegistry.KnownGaps.Count;
        ((double)gapCount / gatedCount).Should().BeLessThan(0.5,
            "known-gap entries must stay a minority of the gated surface — a majority would mean " +
            "the sweep is hiding widespread enforcement debt instead of a handful of pending decisions");
    }

    [UnitTest]
    public void NoHttpSurfaceAllowlist_HasAReasonForEveryEntry()
    {
        foreach (var entry in EntitlementProbeRegistry.NoHttpSurfaceAllowlist.Values)
        {
            entry.Reason.Should().NotBeNullOrWhiteSpace($"no-surface entry '{entry.Key}' must document why it has no probeable HTTP surface");
        }
    }
}
