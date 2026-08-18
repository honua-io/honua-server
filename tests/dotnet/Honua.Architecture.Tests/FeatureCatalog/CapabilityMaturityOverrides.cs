// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Read-only projection of <c>docs/gis/data/capability-maturity-overrides.v1.json</c>
/// (honua-release#100) used by <see cref="FeatureCatalogGenerator"/> to demote the
/// <c>maturity</c> tier of a capability's entries. Mirrors the loader pattern established
/// by <see cref="CapabilityRouteMapper"/> and <see cref="ProofLedgerProjection"/> so all
/// three artifacts are consumed identically by the generator and the drift guards.
/// </summary>
/// <remarks>
/// <para>
/// The runtime maturity lever — a <see cref="CapabilityDescriptor"/> in
/// <c>CapabilityRegistry</c> resolved through <c>CapabilityGateResolver</c> — only reaches
/// the ~30 curated <c>/mcp</c> + <c>honua.capability_manifest.v1</c> ids. A capability key
/// outside that roster therefore had no way to say "shipped, but not advertised as GA in
/// this release", which is why <c>docs/gis/capability-ga-regrade-2026-07.md</c> had to
/// record its <c>scene.catalog</c> demotion as documentation-only. This table is that
/// missing lever, deliberately scoped to the evidence artifacts: it changes what the
/// capability matrix reports and nothing about how the server behaves.
/// </para>
/// <para>
/// Demotion-only by construction: <see cref="ResolveEffective"/> returns the LOWER of the
/// registry-resolved tier and the override tier, so an override can never advertise a
/// capability as more mature than the live registry says. <see cref="Overrides"/> is
/// exposed for the drift guards in <see cref="CapabilityKeyDriftTests"/>.
/// </para>
/// </remarks>
internal sealed class CapabilityMaturityOverrides
{
    private readonly Dictionary<string, CapabilityMaturityOverrideRow> _byCapability;

    private CapabilityMaturityOverrides(IReadOnlyList<CapabilityMaturityOverrideRow> rows)
    {
        Overrides = rows;
        _byCapability = rows.ToDictionary(row => row.Capability, StringComparer.Ordinal);
    }

    /// <summary>Repo-relative location of the committed override artifact.</summary>
    public const string RelativePath = "docs/gis/data/capability-maturity-overrides.v1.json";

    /// <summary>All override rows, in file order — exposed for the drift guards.</summary>
    public IReadOnlyList<CapabilityMaturityOverrideRow> Overrides { get; }

    /// <summary>Loads and parses the committed override document.</summary>
    public static CapabilityMaturityOverrides Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-maturity-overrides.v1.json");

        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, CapabilityMaturityOverrideJsonContext.Default.CapabilityMaturityOverrideDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-maturity-overrides.v1.json.");

        foreach (var row in document.Overrides)
        {
            if (ParseTier(row.Maturity) is null)
            {
                throw new InvalidOperationException(
                    $"capability-maturity-overrides.v1.json row '{row.Capability}' names an unknown maturity tier " +
                    $"'{row.Maturity}'. Valid tiers are the lower-case CapabilityMaturity names.");
            }
        }

        return new CapabilityMaturityOverrides(document.Overrides);
    }

    /// <summary>
    /// Returns the effective maturity tier for a capability: the lower of the
    /// registry-resolved tier and any override row's tier. Demotion-only — an override
    /// never raises a tier.
    /// </summary>
    /// <param name="capability">The capability key the entry resolves to.</param>
    /// <param name="registryMaturity">The tier resolved from the capability registry.</param>
    public string ResolveEffective(string capability, string registryMaturity)
    {
        if (!_byCapability.TryGetValue(capability, out var row))
        {
            return registryMaturity;
        }

        var overrideTier = ParseTier(row.Maturity);
        var registryTier = ParseTier(registryMaturity);

        // An unrecognized registry tier is treated as the most mature so the override
        // still applies; the override tier is validated on load and is never null here.
        return overrideTier is { } demoted && (registryTier is null || demoted < registryTier)
            ? row.Maturity
            : registryMaturity;
    }

    /// <summary>Parses a catalog maturity-tier string into the shared enum, or null.</summary>
    internal static CapabilityMaturity? ParseTier(string tier) => tier switch
    {
        "planned" => CapabilityMaturity.Planned,
        "deferred" => CapabilityMaturity.Deferred,
        "experimental" => CapabilityMaturity.Experimental,
        "partial" => CapabilityMaturity.Partial,
        FeatureCatalogGenerator.MaturityImplemented => CapabilityMaturity.Implemented,
        _ => null,
    };
}

/// <summary>Top-level document for <c>capability-maturity-overrides.v1.json</c>.</summary>
internal sealed class CapabilityMaturityOverrideDocument
{
    /// <summary>Schema version of the override document.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>The reviewed demotion rows.</summary>
    public CapabilityMaturityOverrideRow[] Overrides { get; init; } = [];
}

/// <summary>A single reviewed capability → demoted-maturity row.</summary>
internal sealed class CapabilityMaturityOverrideRow
{
    /// <summary>The capability key whose entries are demoted.</summary>
    public string Capability { get; init; } = string.Empty;

    /// <summary>The demoted maturity tier (lower-case <c>CapabilityMaturity</c> name).</summary>
    public string Maturity { get; init; } = string.Empty;

    /// <summary>Reason code (release-deferred, config-gated-off-by-default, ...).</summary>
    public string ReasonCode { get; init; } = string.Empty;

    /// <summary>Free-text, verifiable reason.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>The decision this row implements.</summary>
    public string Decision { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityMaturityOverrideDocument))]
internal sealed partial class CapabilityMaturityOverrideJsonContext : JsonSerializerContext
{
}
