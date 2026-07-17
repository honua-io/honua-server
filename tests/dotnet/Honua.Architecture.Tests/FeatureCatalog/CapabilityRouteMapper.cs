// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Read-only projection of <c>docs/gis/data/capability-route-mapping.v1.json</c>
/// (issue #2893) used by <see cref="FeatureCatalogGenerator"/> to stamp the
/// <c>capability</c> field on every feature-catalog entry. Mirrors the loader
/// pattern established by <see cref="ProofLedgerProjection"/> so both artifacts
/// are consumed identically by the generator and the drift guard.
/// </summary>
/// <remarks>
/// Rules are evaluated in file order; the first rule whose <c>family</c> matches
/// exactly and whose optional <c>routePrefix</c>/<c>routeContains</c>/<c>method</c>
/// filters all match wins. A family's final, filterless rule is its catch-all
/// default. <see cref="Resolve"/> throws when no rule matches at all — every
/// shipped route must have a mapping before the catalog can regenerate, which is
/// exactly the drift-gated invariant issue #2893 requires (every entry maps to
/// exactly one capability key).
/// </remarks>
internal sealed class CapabilityRouteMapper
{
    private readonly IReadOnlyList<CapabilityRouteRule> _rules;

    private CapabilityRouteMapper(IReadOnlyList<CapabilityRouteRule> rules) => _rules = rules;

    /// <summary>Repo-relative location of the committed route-mapping artifact.</summary>
    public const string RelativePath = "docs/gis/data/capability-route-mapping.v1.json";

    /// <summary>Loads and parses the committed route-mapping document.</summary>
    public static CapabilityRouteMapper Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "capability-route-mapping.v1.json");

        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize(stream, CapabilityRouteMappingJsonContext.Default.CapabilityRouteMappingDocument)
            ?? throw new InvalidOperationException("Unable to deserialize capability-route-mapping.v1.json.");

        return new CapabilityRouteMapper(document.Rules);
    }

    /// <summary>All rules, in file (evaluation) order — exposed for drift/crosswalk tests.</summary>
    public IReadOnlyList<CapabilityRouteRule> Rules => _rules;

    /// <summary>
    /// Resolves the capability key for a route by walking the rules in order and
    /// returning the first match's capability. Throws when no rule matches — a
    /// new route family or operation must get an explicit mapping rule before the
    /// catalog can regenerate.
    /// </summary>
    public string Resolve(string family, string route, string method)
    {
        foreach (var rule in _rules)
        {
            if (!string.Equals(rule.Family, family, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.Method is { Length: > 0 } expectedMethod
                && !string.Equals(expectedMethod, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (rule.RoutePrefix is { Length: > 0 } prefix
                && !route.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (rule.RouteContains is { Length: > 0 } substrings
                && !substrings.Any(substring => route.Contains(substring, StringComparison.Ordinal)))
            {
                continue;
            }

            return rule.Capability;
        }

        throw new InvalidOperationException(
            $"No capability-route-mapping rule matches {method} {route} (family '{family}'). " +
            $"Add a rule to {RelativePath} before regenerating the feature catalog.");
    }
}

/// <summary>Top-level document for <c>capability-route-mapping.v1.json</c>.</summary>
internal sealed class CapabilityRouteMappingDocument
{
    /// <summary>Schema version of the mapping document.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>Ordered mapping rules.</summary>
    public CapabilityRouteRule[] Rules { get; init; } = [];
}

/// <summary>A single ordered route → capability mapping rule.</summary>
internal sealed class CapabilityRouteRule
{
    /// <summary>Exact feature-catalog <c>family</c> this rule applies to.</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>Optional HTTP method filter.</summary>
    public string? Method { get; init; }

    /// <summary>Optional route-prefix filter.</summary>
    public string? RoutePrefix { get; init; }

    /// <summary>Optional route-substring filter (any-of match).</summary>
    public string[]? RouteContains { get; init; }

    /// <summary>The capability key this rule resolves to.</summary>
    public string Capability { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(CapabilityRouteMappingDocument))]
internal sealed partial class CapabilityRouteMappingJsonContext : JsonSerializerContext
{
}
