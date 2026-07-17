// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Drift guard for the capability layer (#2893): every feature-catalog entry
/// maps to exactly one capability key, every canonical capability key has at
/// least one feature-catalog entry or an explicit no-surface annotation, and
/// the committed <c>capability-keys.v1.json</c> snapshot stays in lockstep with
/// <see cref="CapabilityKeyCatalog.All"/> — the same drift-guard shape as
/// <see cref="FeatureCatalogDriftTests"/>.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class CapabilityKeyDriftTests
{
    [ArchitectureTest]
    public void EveryCatalogEntry_HasANonEmptyCapability()
    {
        var catalog = LoadCommittedCatalog();

        var missing = catalog.Entries
            .Where(entry => string.IsNullOrWhiteSpace(entry.Capability))
            .Select(entry => EndpointKey.Format(entry.Method, entry.Route))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            "every feature-catalog entry must resolve to a capability key via "
            + "CapabilityRouteMapper; regenerate with scripts/generate-feature-catalog.sh "
            + "after adding a capability-route-mapping.v1.json rule for any new route.");
    }

    [ArchitectureTest]
    public void EveryCatalogEntry_CapabilityIsInTheCanonicalVocabulary()
    {
        var catalog = LoadCommittedCatalog();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = catalog.Entries
            .Select(entry => entry.Capability)
            .Where(capability => !canonicalKeys.Contains(capability))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        unknown.Should().BeEmpty(
            "every capability key an entry resolves to must exist in "
            + "CapabilityKeyCatalog.All; add it there (and to capability-keys.v1.json) "
            + "before referencing it from a route-mapping rule.");
    }

    [ArchitectureTest]
    public void EveryCanonicalCapability_HasAnEntryOrANoSurfaceAnnotation()
    {
        var catalog = LoadCommittedCatalog();
        var usedCapabilities = catalog.Entries.Select(entry => entry.Capability).ToHashSet(StringComparer.Ordinal);
        var noSurfaceKeys = LoadNoSurfaceAllowlist().NoSurfaceCapabilities
            .Select(entry => entry.Capability)
            .ToHashSet(StringComparer.Ordinal);

        var uncovered = CapabilityKeyCatalog.All
            .Select(capability => capability.Key)
            .Where(key => !usedCapabilities.Contains(key) && !noSurfaceKeys.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        uncovered.Should().BeEmpty(
            "every canonical capability key must have >=1 feature-catalog entry or an "
            + "explicit reason in docs/gis/data/capability-no-surface-allowlist.v1.json "
            + "(issue #2893 acceptance criteria).");
    }

    [ArchitectureTest]
    public void NoSurfaceAllowlist_DoesNotListCapabilitiesThatActuallyHaveEntries()
    {
        // A no-surface entry that later gains a real route must be removed from
        // the allowlist so the artifact does not misrepresent coverage.
        var catalog = LoadCommittedCatalog();
        var usedCapabilities = catalog.Entries.Select(entry => entry.Capability).ToHashSet(StringComparer.Ordinal);
        var noSurfaceKeys = LoadNoSurfaceAllowlist().NoSurfaceCapabilities.Select(entry => entry.Capability);

        var staleAllowlistEntries = noSurfaceKeys
            .Where(key => usedCapabilities.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        staleAllowlistEntries.Should().BeEmpty(
            "these capabilities now have real feature-catalog entries; remove them from "
            + "capability-no-surface-allowlist.v1.json instead of leaving a stale marker.");
    }

    [ArchitectureTest]
    public void RouteMappingRules_OnlyReferenceCanonicalCapabilities()
    {
        var mapper = CapabilityRouteMapper.Load();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = mapper.Rules
            .Select(rule => rule.Capability)
            .Where(capability => !canonicalKeys.Contains(capability))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();

        unknown.Should().BeEmpty(
            "every capability referenced by a capability-route-mapping.v1.json rule must "
            + "exist in CapabilityKeyCatalog.All.");
    }

    [ArchitectureTest]
    public void CommittedCapabilityKeysJson_MatchesCapabilityKeyCatalog()
    {
        var committed = LoadCommittedCapabilityKeysDocument();
        var live = CapabilityKeyCatalog.All
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();

        var committedByKey = committed.Capabilities
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();

        committedByKey.Select(c => c.Key).Should().Equal(live.Select(c => c.Key),
            "docs/gis/data/capability-keys.v1.json's capability key set must equal "
            + "CapabilityKeyCatalog.All; regenerate the artifact after changing the catalog.");

        for (var i = 0; i < live.Length; i++)
        {
            var expected = live[i];
            var actual = committedByKey[i];

            actual.DisplayName.Should().Be(expected.DisplayName, "capability '{0}' displayName drifted", expected.Key);
            actual.Category.Should().Be(expected.Category, "capability '{0}' category drifted", expected.Key);
            actual.Edition.Should().Be(expected.Edition.ToString(), "capability '{0}' edition drifted", expected.Key);
            actual.Description.Should().Be(expected.Description, "capability '{0}' description drifted", expected.Key);
        }
    }

    private static FeatureCatalog LoadCommittedCatalog()
    {
        var path = FeatureCatalogPaths.CommittedArtifactPath();
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, FeatureCatalogJsonContext.Default.FeatureCatalog)
            ?? throw new InvalidOperationException("Unable to deserialize feature-catalog.json.");
    }

    private static CapabilityNoSurfaceAllowlistDocument LoadNoSurfaceAllowlist()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-no-surface-allowlist.v1.json");

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, CapabilityAllowlistJsonContext.Default.CapabilityNoSurfaceAllowlistDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-no-surface-allowlist.v1.json.");
    }

    internal static CapabilityKeysDocument LoadCommittedCapabilityKeysDocument()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-keys.v1.json");

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, CapabilityKeysJsonContext.Default.CapabilityKeysDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-keys.v1.json.");
    }
}

/// <summary>Top-level document for <c>capability-keys.v1.json</c>.</summary>
internal sealed class CapabilityKeysDocument
{
    /// <summary>Schema version.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>The full canonical capability list.</summary>
    public CapabilityKeyJson[] Capabilities { get; init; } = [];

    /// <summary>Crosswalk sections.</summary>
    public CapabilityCrosswalksJson Crosswalks { get; init; } = new();
}

/// <summary>A single capability entry as committed to JSON.</summary>
internal sealed class CapabilityKeyJson
{
    /// <summary>Capability key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Display name.</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Category.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Edition (string form of <see cref="HonuaEdition"/>).</summary>
    public string Edition { get; init; } = string.Empty;

    /// <summary>Description.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>The four crosswalk sections.</summary>
internal sealed class CapabilityCrosswalksJson
{
    /// <summary>honua-esri-assess verdict-registry crosswalk rows.</summary>
    public EsriAssessCrosswalkRow[] EsriAssess { get; init; } = [];

    /// <summary>Client-interop (client_lane x protocol) crosswalk rows.</summary>
    public InteropCrosswalkRow[] Interop { get; init; } = [];

    /// <summary>geoservices-rest-parity.json service-id crosswalk rows.</summary>
    public EsriCompatMatrixCrosswalkRow[] EsriCompatMatrix { get; init; } = [];

    /// <summary>geobench scenario crosswalk rows.</summary>
    public GeobenchCrosswalkRow[] Geobench { get; init; } = [];
}

/// <summary>A single honua-esri-assess crosswalk row.</summary>
internal sealed class EsriAssessCrosswalkRow
{
    /// <summary>The assess registry key.</summary>
    public string AssessKey { get; init; } = string.Empty;

    /// <summary>The capability key it maps to.</summary>
    public string Capability { get; init; } = string.Empty;
}

/// <summary>A single client-interop crosswalk row.</summary>
internal sealed class InteropCrosswalkRow
{
    /// <summary>The client_lane value.</summary>
    public string ClientLane { get; init; } = string.Empty;

    /// <summary>The protocol value.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>The capability key it maps to.</summary>
    public string Capability { get; init; } = string.Empty;
}

/// <summary>A single esri-compat matrix crosswalk row.</summary>
internal sealed class EsriCompatMatrixCrosswalkRow
{
    /// <summary>The geoservices-rest-parity.json service id.</summary>
    public string ServiceId { get; init; } = string.Empty;

    /// <summary>The capability key it maps to.</summary>
    public string Capability { get; init; } = string.Empty;
}

/// <summary>A single geobench crosswalk row.</summary>
internal sealed class GeobenchCrosswalkRow
{
    /// <summary>The geobench scenario name.</summary>
    public string Scenario { get; init; } = string.Empty;

    /// <summary>The capability key it maps to.</summary>
    public string Capability { get; init; } = string.Empty;
}

/// <summary>Top-level document for <c>capability-no-surface-allowlist.v1.json</c>.</summary>
internal sealed class CapabilityNoSurfaceAllowlistDocument
{
    /// <summary>Schema version.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>The allowlisted no-surface capabilities.</summary>
    public NoSurfaceCapabilityEntry[] NoSurfaceCapabilities { get; init; } = [];
}

/// <summary>A single no-surface allowlist entry.</summary>
internal sealed class NoSurfaceCapabilityEntry
{
    /// <summary>The capability key with no distinct route.</summary>
    public string Capability { get; init; } = string.Empty;

    /// <summary>Reason code (config-flag, sdk-only, ...).</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Free-text reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityKeysDocument))]
internal sealed partial class CapabilityKeysJsonContext : JsonSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityNoSurfaceAllowlistDocument))]
internal sealed partial class CapabilityAllowlistJsonContext : JsonSerializerContext
{
}
