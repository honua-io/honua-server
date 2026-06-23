// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server;
using Honua.TestKit.Attributes;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Deterministic projection of the shipped HTTP API surface into the
/// evidence-based feature catalog (<c>docs/gis/data/feature-catalog.json</c>,
/// tracking issue #1946, ADR-0054).
/// </summary>
/// <remarks>
/// <para>
/// The catalog is <b>generated, never authored</b>. Every entry is a join of
/// three already-enforced sources:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <see cref="EndpointRegistry.All"/> — the canonical list of every deployed
///     route (method + path).
///   </description></item>
///   <item><description>
///     the <c>[Endpoint("METHOD /...")]</c>-attributed integration tests
///     discovered via <see cref="ArchitectureTestHelpers.IntegrationTestMethods"/>
///     — the capability→proving-test links.
///   </description></item>
///   <item><description>
///     the public-interface proof ledger
///     (<c>docs/gis/data/public-interface-proof.json</c>) — the proof-ledger
///     surface and its mapped source (<c>code_location</c>).
///   </description></item>
/// </list>
/// <para>
/// Slice 1 (#1946) projects the API surface only. <c>maturity</c> is
/// <c>"implemented"</c> for every registry route that has at least one proving
/// test. Status-from-CI, partial/deferred maturity wiring, OperationRegistry /
/// MCP-tool coverage, and the <c>geoservices-parity.md</c> retirement are later
/// slices (see ADR-0054 / #1946).
/// </para>
/// </remarks>
internal static class FeatureCatalogGenerator
{
    /// <summary>Catalog schema version. Bump when the entry shape changes.</summary>
    public const string SchemaVersion = "1.0.0";

    /// <summary>Slice 1 maturity tier for every implemented, test-backed route.</summary>
    public const string MaturityImplemented = "implemented";

    /// <summary>
    /// Builds the catalog model deterministically (stable ordering by method then
    /// path) from the live registry, the proving-test attributes, and the proof
    /// ledger. The same method backs both the emitter that writes the committed
    /// artifact and the drift guard that compares against it, so the two can
    /// never disagree.
    /// </summary>
    public static FeatureCatalog Generate()
    {
        var provingTestsByEndpoint = CollectProvingTests();
        var ledger = ProofLedgerProjection.Load();

        var entries = EndpointRegistry.All
            // EndpointRegistry.All may list the same (method, route) twice when a
            // route is grouped under two comment sections (e.g. the forms-package
            // generate route). The existing governance tests dedupe via HashSets;
            // the catalog projects one entry per distinct route so ids stay unique.
            .DistinctBy(endpoint => EndpointKey.Format(endpoint.Method, endpoint.Path), StringComparer.OrdinalIgnoreCase)
            .Select(endpoint =>
            {
                var key = EndpointKey.Format(endpoint.Method, endpoint.Path);
                var provingTests = provingTestsByEndpoint.TryGetValue(key, out var tests)
                    ? tests.OrderBy(value => value, StringComparer.Ordinal).ToArray()
                    : [];

                var surface = ledger.ResolveSurface(endpoint.Path);

                return new FeatureCatalogEntry
                {
                    Id = SlugFor(endpoint.Method, endpoint.Path),
                    Route = endpoint.Path,
                    Method = endpoint.Method.ToUpperInvariant(),
                    Family = surface?.Protocol ?? "uncategorized",
                    Protocol = surface?.SurfaceKind ?? "http-route",
                    CodeLocation = surface?.CodeLocation ?? "src/Honua.Server/EndpointRegistry.cs",
                    ProvingTests = provingTests,
                    ProofLedgerSurface = surface?.SurfaceId ?? string.Empty,
                    Maturity = MaturityImplemented
                };
            })
            .OrderBy(entry => entry.Method, StringComparer.Ordinal)
            .ThenBy(entry => entry.Route, StringComparer.Ordinal)
            .ToArray();

        return new FeatureCatalog
        {
            SchemaVersion = SchemaVersion,
            Generator = "tests/dotnet/Honua.Architecture.Tests/FeatureCatalog/FeatureCatalogGenerator.cs",
            TrackingIssue = "#1946",
            Slice = "slice-1-api-surface",
            Entries = entries
        };
    }

    /// <summary>
    /// Serializes the catalog with stable, human-diffable formatting (indented,
    /// trailing newline). Determinism is essential: the drift guard compares the
    /// committed file byte-for-byte against this output. Newlines are pinned to
    /// LF so the artifact is reproducible cross-platform — the indented
    /// <see cref="System.Text.Json"/> writer otherwise emits
    /// <see cref="Environment.NewLine"/> (CRLF on Windows), which would not match
    /// the LF-committed file generated by Linux CI.
    /// </summary>
    public static string Serialize(FeatureCatalog catalog)
        => JsonSerializer.Serialize(catalog, FeatureCatalogJsonContext.Default.FeatureCatalog)
            .ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Maps every <c>[Endpoint("METHOD /...")]</c> integration test to the
    /// normalized endpoint key it proves. A single endpoint may be proven by many
    /// tests; a single test may prove many endpoints.
    /// </summary>
    private static Dictionary<string, HashSet<string>> CollectProvingTests()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var method in ArchitectureTestHelpers.IntegrationTestMethods())
        {
            var testId = $"{method.DeclaringType?.FullName}.{method.Name}";
            foreach (var endpointAttribute in method
                         .GetCustomAttributes(typeof(EndpointAttribute), inherit: true)
                         .Cast<EndpointAttribute>())
            {
                var key = EndpointKey.Normalize(endpointAttribute.Endpoint);
                if (key is null)
                {
                    continue;
                }

                if (!map.TryGetValue(key, out var ids))
                {
                    ids = new HashSet<string>(StringComparer.Ordinal);
                    map[key] = ids;
                }

                ids.Add(testId);
            }
        }

        return map;
    }

    /// <summary>
    /// Produces a stable, lower-kebab id from the method and route so catalog
    /// entries have a human-readable, diff-friendly key independent of ordering.
    /// </summary>
    private static string SlugFor(string method, string path)
    {
        var raw = $"{method}{path}".ToLowerInvariant();
        var chars = raw
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();

        var slug = new string(chars);
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return slug.Trim('-');
    }
}

/// <summary>Normalizes endpoint keys shared by the generator and the drift guard.</summary>
internal static class EndpointKey
{
    /// <summary>Formats a method/path pair into the canonical <c>METHOD /path</c> key.</summary>
    public static string Format(string method, string path)
    {
        var normalizedPath = path.Trim();
        if (!normalizedPath.StartsWith('/'))
        {
            normalizedPath = "/" + normalizedPath;
        }

        return $"{method.Trim().ToUpperInvariant()} {normalizedPath}";
    }

    /// <summary>
    /// Normalizes an <c>[Endpoint]</c> attribute value (<c>"METHOD /path"</c>)
    /// into the canonical key, or <c>null</c> when it cannot be parsed.
    /// </summary>
    public static string? Normalize(string endpoint)
    {
        var parts = endpoint.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length != 2 ? null : Format(parts[0], parts[1]);
    }
}

/// <summary>Root document for <c>feature-catalog.json</c>.</summary>
internal sealed class FeatureCatalog
{
    /// <summary>Schema version of the catalog document.</summary>
    public string SchemaVersion { get; init; } = string.Empty;

    /// <summary>Relative path of the generator that produced this artifact.</summary>
    public string Generator { get; init; } = string.Empty;

    /// <summary>Epic this catalog belongs to (#1946).</summary>
    public string TrackingIssue { get; init; } = string.Empty;

    /// <summary>Slice marker; slice 1 projects the API surface only.</summary>
    public string Slice { get; init; } = string.Empty;

    /// <summary>Catalog entries, ordered by method then route.</summary>
    public FeatureCatalogEntry[] Entries { get; init; } = [];
}

/// <summary>A single API-surface capability projection.</summary>
internal sealed class FeatureCatalogEntry
{
    /// <summary>Stable kebab-case id derived from method + route.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Route pattern as registered in <see cref="EndpointRegistry"/>.</summary>
    public string Route { get; init; } = string.Empty;

    /// <summary>HTTP method.</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Capability family (proof-ledger surface protocol label).</summary>
    public string Family { get; init; } = string.Empty;

    /// <summary>Protocol/surface kind from the proof ledger.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Mapped source location (proof-ledger evidence).</summary>
    public string CodeLocation { get; init; } = string.Empty;

    /// <summary>Ids of the <c>[Endpoint]</c>-attributed proving tests covering this route.</summary>
    public string[] ProvingTests { get; init; } = [];

    /// <summary>Proof-ledger surface id this route maps to.</summary>
    public string ProofLedgerSurface { get; init; } = string.Empty;

    /// <summary>Maturity tier. Slice 1 is always <c>implemented</c>.</summary>
    public string Maturity { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(FeatureCatalog))]
internal sealed partial class FeatureCatalogJsonContext : JsonSerializerContext
{
}
