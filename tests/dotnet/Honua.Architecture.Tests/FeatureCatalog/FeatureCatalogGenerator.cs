// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;
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
/// Slice 1 (#1946) projects the API surface only. <c>maturity</c> is read from
/// the unified capability registry (<see cref="ICapabilityRegistry"/>,
/// ADR-0058) rather than hard-coded: each entry's proof-ledger surface / route
/// id is joined to a <see cref="CapabilityDescriptor"/> and the descriptor's
/// <see cref="CapabilityMaturity"/> is emitted (#2345 / T9). A route with no
/// matching descriptor falls back to <c>"implemented"</c>, which — given the
/// API-surface coverage gate — is every route that has at least one proving
/// test. Because the registry currently marks every descriptor
/// <see cref="CapabilityMaturity.Implemented"/>, this wiring is value-preserving
/// today; flipping a descriptor to <see cref="CapabilityMaturity.Experimental"/>
/// (T10 / #2346) now flows through to the catalog and, via the drift guard,
/// requires regeneration. Status-from-CI, partial/deferred maturity wiring,
/// OperationRegistry / MCP-tool coverage, and the <c>geoservices-parity.md</c>
/// retirement are later slices (see ADR-0054 / #1946).
/// </para>
/// </remarks>
internal static class FeatureCatalogGenerator
{
    /// <summary>Catalog schema version. Bump when the entry shape changes.</summary>
    public const string SchemaVersion = "1.0.0";

    /// <summary>
    /// Fallback maturity tier for a test-backed route that has no matching
    /// capability descriptor in the registry.
    /// </summary>
    public const string MaturityImplemented = "implemented";

    /// <summary>
    /// Maturity tier for a built-experimental route group flipped in T10 (#2346):
    /// gated OFF the first-release surface, opt-in per capability.
    /// </summary>
    public const string MaturityExperimental = "experimental";

    /// <summary>
    /// The unified capability registry (ADR-0058) the generator joins against for
    /// each entry's <c>maturity</c>. It is a pure, static-backed projection, so a
    /// single shared instance is safe.
    /// </summary>
    private static readonly CapabilityRegistry Registry = new CapabilityRegistry();

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
                var id = SlugFor(endpoint.Method, endpoint.Path);

                return new FeatureCatalogEntry
                {
                    Id = id,
                    Route = endpoint.Path,
                    Method = endpoint.Method.ToUpperInvariant(),
                    Family = surface?.Protocol ?? "uncategorized",
                    Protocol = surface?.SurfaceKind ?? "http-route",
                    CodeLocation = surface?.CodeLocation ?? "src/Honua.Server/EndpointRegistry.cs",
                    ProvingTests = provingTests,
                    ProofLedgerSurface = surface?.SurfaceId ?? string.Empty,
                    Maturity = ResolveMaturity(surface?.SurfaceId, id, endpoint.Path)
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

    /// <summary>
    /// Joins an entry to its capability descriptor and returns the descriptor's
    /// maturity. The join is attempted, in order, by (1) the proof-ledger surface
    /// id, (2) the entry's own route slug, and (3) the route → descriptor map
    /// (<see cref="ResolveDescriptorIdForRoute"/>). The third join is the one T10
    /// (#2346) wires: the proof-ledger surfaces and route slugs never equalled a
    /// registry descriptor id (the <c>control-plane-admin</c> surface, for example,
    /// is a coarse bucket spanning many unrelated admin routes), so the
    /// surface/slug joins were inert; the route-prefix map connects the flipped
    /// route groups to their descriptor so the catalog reflects <c>experimental</c>
    /// for exactly the routes the T5 endpoint gate 404s. Falls back to
    /// <see cref="MaturityImplemented"/> when no descriptor claims the route.
    /// </summary>
    private static string ResolveMaturity(string? proofLedgerSurfaceId, string entryId, string route)
    {
        var routeDescriptorId = ResolveDescriptorIdForRoute(route);
        var descriptor =
            (string.IsNullOrEmpty(proofLedgerSurfaceId) ? null : Registry.Find(proofLedgerSurfaceId))
            ?? Registry.Find(entryId)
            ?? (routeDescriptorId is null ? null : Registry.Find(routeDescriptorId));

        return descriptor is null ? MaturityImplemented : MaturityLabel(descriptor.Maturity);
    }

    /// <summary>
    /// Maps a shipped route to the capability descriptor that gates it, for the
    /// built-experimental feature groups flipped in T10 (#2346). This is the single
    /// projection of the same route groups the <c>WithCapabilityGate(...)</c> calls
    /// guard in <c>Honua.Server</c>, so the catalog's <c>experimental</c> tier and
    /// the runtime 404 gate stay in lock-step. Returns <c>null</c> for every route
    /// that is not part of a flipped group (the in-release surface), which then
    /// falls back to <c>implemented</c>.
    /// </summary>
    internal static string? ResolveDescriptorIdForRoute(string route)
    {
        // Temporal analytics — /api/v1/temporal/* was promoted to GA (Implemented) in
        // #2429 (temporal.filtering/extent-discovery/histogram/time-series-tiles), so it is
        // no longer a flipped experimental group: its routes fall through to the in-release
        // surface (implemented) like any other GA capability. Edition entitlements
        // (Community vs Pro) still apply, but those are not an experimental-maturity concern.

        // Geofence alerting — /api/v1/admin/alerts/* was promoted to GA (Implemented)
        // in #2427, so it is no longer a flipped experimental group: its routes fall
        // through to the in-release surface (implemented) like any other GA capability.

        // Native mTLS (client certificates) — /api/v1/admin/security/client-certificates/*
        if (route.StartsWith("/api/v1/admin/security/client-certificates", StringComparison.OrdinalIgnoreCase))
        {
            return "security.mtls";
        }

        // Disconnected-sync replica / conflict review — /api/v1/admin/services/{serviceId}/replicas/*
        if (route.StartsWith("/api/v1/admin/services/", StringComparison.OrdinalIgnoreCase) &&
            route.Contains("/replicas", StringComparison.OrdinalIgnoreCase))
        {
            return "sync.offline";
        }

        // Realtime feature streaming — /api/v1/streaming/features*, the admin
        // session-visibility and streaming-operations surfaces.
        if (route.StartsWith("/api/v1/streaming/features", StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith("/api/v1/admin/streaming/features", StringComparison.OrdinalIgnoreCase) ||
            route.StartsWith("/api/v1/admin/operations/streaming", StringComparison.OrdinalIgnoreCase))
        {
            return "realtime.feature-streams";
        }

        // Branch versioning (VMS REST surface) — /rest/services/{serviceId}/VersionManagementServer/*
        // Gated Preview in the BH6-001/BH6-002 fix batch (ADR-0058).
        if (route.StartsWith("/rest/services/", StringComparison.OrdinalIgnoreCase) &&
            route.Contains("/VersionManagementServer", StringComparison.OrdinalIgnoreCase))
        {
            return "versioning.branch";
        }

        return null;
    }

    /// <summary>
    /// Projects the shared <see cref="CapabilityMaturity"/> enum onto the catalog's
    /// lower-case maturity-tier string (matching the ADR-0054 tier names).
    /// </summary>
    private static string MaturityLabel(CapabilityMaturity maturity)
        => maturity switch
        {
            CapabilityMaturity.Planned => "planned",
            CapabilityMaturity.Deferred => "deferred",
            CapabilityMaturity.Experimental => "experimental",
            CapabilityMaturity.Partial => "partial",
            CapabilityMaturity.Implemented => MaturityImplemented,
            _ => MaturityImplemented,
        };
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

    /// <summary>
    /// Maturity tier, read from the matching capability-registry descriptor
    /// (ADR-0058) and falling back to <c>implemented</c> for routes with no
    /// descriptor.
    /// </summary>
    public string Maturity { get; init; } = string.Empty;
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true)]
[JsonSerializable(typeof(FeatureCatalog))]
internal sealed partial class FeatureCatalogJsonContext : JsonSerializerContext
{
}
