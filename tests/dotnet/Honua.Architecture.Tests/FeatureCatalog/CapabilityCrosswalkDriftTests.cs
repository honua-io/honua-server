// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Drift guard for the capability-crosswalk artifact (#2893): every crosswalk
/// source key from the four evidence vocabularies (honua-esri-assess,
/// client-interop, esri-compat parity, geobench) maps to exactly one canonical
/// capability key, and every known Esri hard lock-in is an explicit
/// <c>not-supported</c> marker rather than a silent gap.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class CapabilityCrosswalkDriftTests
{
    /// <summary>
    /// The honua-esri-assess verdict registry's hard-lock-in ("no-go") keys
    /// (<c>src/honua_esri_assess/verdict/registry.py</c>, <c>HARD_LOCK_IN_KEYS</c>,
    /// read-only reference — this repo does not modify that registry). Mirrored
    /// here so the drift guard can assert they never silently reappear in the
    /// mapped-capability crosswalk and always appear in the no-go allowlist.
    /// </summary>
    private static readonly string[] EsriAssessHardLockInKeys = ["utility-network", "parcel-fabric", "lrs"];

    [ArchitectureTest]
    public void EveryEsriAssessCrosswalkRow_ResolvesToACanonicalCapability()
    {
        var document = LoadCapabilityKeys();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = document.Crosswalks.EsriAssess
            .Where(row => !canonicalKeys.Contains(row.Capability))
            .Select(row => $"{row.AssessKey} -> {row.Capability}")
            .ToArray();

        unknown.Should().BeEmpty("every esriAssess crosswalk row must map to a capability key that exists in CapabilityKeyCatalog.All.");
    }

    [ArchitectureTest]
    public void EveryInteropCrosswalkRow_ResolvesToACanonicalCapability()
    {
        var document = LoadCapabilityKeys();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = document.Crosswalks.Interop
            .Where(row => !canonicalKeys.Contains(row.Capability))
            .Select(row => $"{row.ClientLane}/{row.Protocol} -> {row.Capability}")
            .ToArray();

        unknown.Should().BeEmpty("every interop crosswalk row must map to a capability key that exists in CapabilityKeyCatalog.All.");
    }

    [ArchitectureTest]
    public void EveryEsriCompatMatrixCrosswalkRow_ResolvesToACanonicalCapability()
    {
        var document = LoadCapabilityKeys();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = document.Crosswalks.EsriCompatMatrix
            .Where(row => !canonicalKeys.Contains(row.Capability))
            .Select(row => $"{row.ServiceId} -> {row.Capability}")
            .ToArray();

        unknown.Should().BeEmpty("every esriCompatMatrix crosswalk row must map to a capability key that exists in CapabilityKeyCatalog.All.");
    }

    [ArchitectureTest]
    public void EveryGeobenchCrosswalkRow_ResolvesToACanonicalCapability()
    {
        var document = LoadCapabilityKeys();
        var canonicalKeys = CapabilityKeyCatalog.All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

        var unknown = document.Crosswalks.Geobench
            .Where(row => !canonicalKeys.Contains(row.Capability))
            .Select(row => $"{row.Scenario} -> {row.Capability}")
            .ToArray();

        unknown.Should().BeEmpty("every geobench crosswalk row must map to a capability key that exists in CapabilityKeyCatalog.All.");
    }

    [ArchitectureTest]
    public void EsriCompatMatrixServiceIds_AreCoveredByTheParityIndex()
    {
        // Every geoservices-rest-parity.json service id the crosswalk claims
        // must actually exist in the parity index -- catches a stale crosswalk
        // row after a parity-index rename.
        var document = LoadCapabilityKeys();
        var parityServiceIds = LoadParityServiceIds();

        var unknown = document.Crosswalks.EsriCompatMatrix
            .Select(row => row.ServiceId)
            .Where(serviceId => !parityServiceIds.Contains(serviceId))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        unknown.Should().BeEmpty(
            "every esriCompatMatrix crosswalk row's serviceId must exist in "
            + "docs/gis/data/geoservices-rest-parity.json's services[].id.");
    }

    [ArchitectureTest]
    public void EveryEsriAssessHardLockInKey_IsInTheNoGoAllowlist_NotTheMappedCrosswalk()
    {
        var document = LoadCapabilityKeys();
        var noGoAllowlist = LoadNoGoAllowlist();

        var mappedAssessKeys = document.Crosswalks.EsriAssess
            .Select(row => row.AssessKey)
            .ToHashSet(StringComparer.Ordinal);
        var noGoKeys = noGoAllowlist.NotSupported
            .Where(entry => string.Equals(entry.Source, "esri-assess", StringComparison.Ordinal))
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var hardLockInKey in EsriAssessHardLockInKeys)
        {
            mappedAssessKeys.Should().NotContain(
                hardLockInKey,
                "'{0}' is a honua-esri-assess hard Esri lock-in and must never be silently "
                + "mapped to a capability in the esriAssess crosswalk — it belongs only in "
                + "capability-no-go-allowlist.v1.json.",
                hardLockInKey);

            noGoKeys.Should().Contain(
                hardLockInKey,
                "'{0}' is a honua-esri-assess hard Esri lock-in and must have an explicit "
                + "not-supported marker in capability-no-go-allowlist.v1.json.",
                hardLockInKey);
        }
    }

    [ArchitectureTest]
    public void NoGoAllowlist_EveryEntryHasAMarkerAndBoundary()
    {
        var noGoAllowlist = LoadNoGoAllowlist();

        foreach (var entry in noGoAllowlist.NotSupported)
        {
            entry.Marker.Should().Be("not-supported");
            entry.Boundary.Should().NotBeNullOrWhiteSpace();
            entry.Source.Should().NotBeNullOrWhiteSpace();
            entry.Key.Should().NotBeNullOrWhiteSpace();
        }
    }

    private static CapabilityKeysDocument LoadCapabilityKeys()
        => CapabilityKeyDriftTests.LoadCommittedCapabilityKeysDocument();

    private static HashSet<string> LoadParityServiceIds()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "geoservices-rest-parity.json");

        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);

        return document.RootElement.GetProperty("services")
            .EnumerateArray()
            .Select(service => service.GetProperty("id").GetString() ?? string.Empty)
            .Where(id => id.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static CapabilityNoGoAllowlistDocument LoadNoGoAllowlist()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-no-go-allowlist.v1.json");

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, CapabilityNoGoAllowlistJsonContext.Default.CapabilityNoGoAllowlistDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-no-go-allowlist.v1.json.");
    }
}

/// <summary>Top-level document for <c>capability-no-go-allowlist.v1.json</c>.</summary>
internal sealed class CapabilityNoGoAllowlistDocument
{
    /// <summary>Schema version.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>The explicit not-supported entries.</summary>
    public NoGoAllowlistEntry[] NotSupported { get; init; } = [];
}

/// <summary>A single not-supported allowlist entry.</summary>
internal sealed class NoGoAllowlistEntry
{
    /// <summary>The originating evidence vocabulary (e.g. <c>esri-assess</c>).</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The source vocabulary's key.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>Must always be <c>not-supported</c>.</summary>
    public string Marker { get; init; } = string.Empty;

    /// <summary>Plain-language migration-boundary description.</summary>
    public string Boundary { get; init; } = string.Empty;

    /// <summary>Reference to the source-of-truth registry entry.</summary>
    public string Reference { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityNoGoAllowlistDocument))]
internal sealed partial class CapabilityNoGoAllowlistJsonContext : JsonSerializerContext
{
}
