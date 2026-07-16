// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Architecture.Tests.FeatureCatalog;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Partial drift guard for the published GeoServices REST parity matrix
/// (<c>docs/gis/data/geoservices-rest-parity.json</c>, #2861). The matrix is the
/// most externally-consequential capability roster we publish — GitBook maps five
/// public URLs onto its <c>.md</c> summary — and until this guard it was
/// hand-maintained with nothing checking it against the code.
/// <para>
/// Unlike <c>feature-catalog.json</c>, the matrix cannot be fully generated: the
/// Implemented/Partial/Stub/Not-implemented vocabulary encodes human judgement about
/// *how much* of an Esri operation's documented behaviour is supported, which is not
/// derivable from a route table. So this guard deliberately enforces only what is
/// mechanically decidable, in the direction that costs trust:
/// </para>
/// <list type="number">
///   <item><description>
///     every endpoint the matrix claims as implemented/partial/stub actually exists
///     on the served surface (<see cref="EndpointRegistry.All"/>) — catches the
///     over-claim that a prospect discovers by getting a 404;
///   </description></item>
///   <item><description>
///     no operation recorded as not-implemented is in fact served — catches the
///     under-claim direction (shipped work still recorded as deferred, e.g. the
///     async exportTiles lifecycle that #2861 found stale);
///   </description></item>
///   <item><description>
///     every <c>evidence</c> path resolves to a real file — the matrix cited 24 paths
///     that no longer existed after GeoServices moved assemblies.
///   </description></item>
/// </list>
/// <para>
/// <b>What remains ungated, stated plainly:</b> whether a status is the *right* status.
/// Nothing here stops someone labelling a Stub as Implemented, or a Partial whose
/// parameter coverage has silently regressed. Those need human review — the matrix's
/// <c>lastReviewed</c> date and the release checklist remain the control. This guard
/// makes the route-level claims honest, not the behavioural ones. It must never be
/// used to justify upgrading a Stub to Implemented to make a test pass.
/// </para>
/// </summary>
[Trait("Category", "Architecture")]
public sealed class GeoServicesParityMatrixDriftTests
{
    /// <summary>Repo-relative location of the published machine-readable matrix.</summary>
    private const string RelativePath = "docs/gis/data/geoservices-rest-parity.json";

    /// <summary>Statuses whose entries assert that Honua serves something.</summary>
    private static readonly string[] ServedBuckets = ["implemented", "partial", "stub"];

    /// <summary>Route-constraint stripper: <c>{layerId:int}</c> → <c>{layerId}</c>.</summary>
    private static readonly Regex RouteConstraintRegex = new(
        @"\{(?<name>[^{}:]+):[^{}]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [ArchitectureTest]
    public void EveryServedClaim_ResolvesToARegisteredRoute()
    {
        var registered = EndpointRegistry.All
            .Select(endpoint => EndpointKey.Format(endpoint.Method, endpoint.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unresolved = new List<string>();

        foreach (var (serviceId, bucket, name, endpoint) in EnumerateClaimedEndpoints())
        {
            if (!ServedBuckets.Contains(bucket, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = EndpointKey.Normalize(NormalizeRoute(endpoint));
            if (key is null)
            {
                unresolved.Add($"{serviceId}/{name}: '{endpoint}' is not a 'METHOD /path' pair");
                continue;
            }

            if (!registered.Contains(key))
            {
                unresolved.Add($"{serviceId}/{name} ({bucket}): {endpoint}");
            }
        }

        unresolved.Should().BeEmpty(
            "every endpoint the published parity matrix claims as implemented/partial/stub must exist in "
            + "EndpointRegistry.All. A claim that resolves to no route is an over-claim: a prospect who tests it "
            + "gets a 404. Fix the matrix (or the route) — do not delete the assertion.");
    }

    [ArchitectureTest]
    public void EveryNotImplementedClaim_IsAbsentFromTheServedSurface()
    {
        var registeredPaths = EndpointRegistry.All
            .Select(endpoint => endpoint.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var served = new List<string>();

        foreach (var (serviceId, name, esriPath) in EnumerateNotImplementedPaths())
        {
            // Wildcard families (e.g. '/MapServer/exts/*') describe a class of routes
            // rather than one path; they are not decidable by exact match.
            if (esriPath.Contains('*', StringComparison.Ordinal))
            {
                continue;
            }

            if (registeredPaths.Contains(NormalizeRoute(esriPath)))
            {
                served.Add($"{serviceId}/{name}: {esriPath}");
            }
        }

        served.Should().BeEmpty(
            "an operation recorded as not-implemented must not actually be served. This is the under-claim "
            + "direction: shipped work still published as deferred (it cost us credit for the async exportTiles "
            + "lifecycle in #2861). Promote the entry to its honest status instead.");
    }

    [ArchitectureTest]
    public void EveryEvidencePath_ResolvesToARealFile()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var missing = new List<string>();

        using var document = JsonDocument.Parse(File.ReadAllText(CommittedArtifactPath()));

        foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetString() ?? "(unknown)";
            if (!service.TryGetProperty("evidence", out var evidence))
            {
                continue;
            }

            foreach (var kind in evidence.EnumerateObject())
            {
                foreach (var path in kind.Value.EnumerateArray())
                {
                    var relative = path.GetString();
                    if (string.IsNullOrWhiteSpace(relative))
                    {
                        continue;
                    }

                    var absolute = ArchitectureTestHelpers.CombinePath(
                        root, relative.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(absolute))
                    {
                        missing.Add($"{serviceId}/{kind.Name}: {relative}");
                    }
                }
            }
        }

        missing.Should().BeEmpty(
            "every evidence path cited by the parity matrix must resolve to a real file; a dead path means the "
            + "matrix is citing code that moved or was deleted, and a reader cannot verify the claim.");
    }

    /// <summary>
    /// Yields (serviceId, bucket, name, endpoint) for every <c>honuaEndpoints</c> entry
    /// under a service's <c>operations</c> and <c>childResources</c> status buckets.
    /// </summary>
    private static IEnumerable<(string ServiceId, string Bucket, string Name, string Endpoint)> EnumerateClaimedEndpoints()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CommittedArtifactPath()));

        foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetString() ?? "(unknown)";

            // `honuaExtensions` is a flat array of served entries (no status buckets):
            // Honua-specific operations with no Esri equivalent. Treat it as implemented.
            if (service.TryGetProperty("honuaExtensions", out var extensions)
                && extensions.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in extensions.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object
                        || !entry.TryGetProperty("honuaEndpoints", out var extEndpoints))
                    {
                        continue;
                    }

                    var extName = entry.TryGetProperty("name", out var en) ? en.GetString() ?? "(unnamed)" : "(unnamed)";
                    foreach (var endpoint in extEndpoints.EnumerateArray())
                    {
                        var value = endpoint.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            yield return (serviceId, "implemented", extName, value);
                        }
                    }
                }
            }

            foreach (var container in new[] { "operations", "childResources" })
            {
                if (!service.TryGetProperty(container, out var buckets)
                    || buckets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var bucket in buckets.EnumerateObject())
                {
                    if (bucket.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in bucket.Value.EnumerateArray())
                    {
                        // Some buckets carry bare strings (a name only, no route claim).
                        if (entry.ValueKind != JsonValueKind.Object
                            || !entry.TryGetProperty("honuaEndpoints", out var endpoints))
                        {
                            continue;
                        }

                        var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "(unnamed)" : "(unnamed)";
                        foreach (var endpoint in endpoints.EnumerateArray())
                        {
                            var value = endpoint.GetString();
                            if (!string.IsNullOrWhiteSpace(value))
                            {
                                yield return (serviceId, NormalizeBucket(bucket.Name), name, value);
                            }
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Yields (serviceId, name, esriPath) for every not-implemented entry carrying a concrete path.
    /// </summary>
    private static IEnumerable<(string ServiceId, string Name, string EsriPath)> EnumerateNotImplementedPaths()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(CommittedArtifactPath()));

        foreach (var service in document.RootElement.GetProperty("services").EnumerateArray())
        {
            var serviceId = service.GetProperty("id").GetString() ?? "(unknown)";

            foreach (var container in new[] { "operations", "childResources" })
            {
                if (!service.TryGetProperty(container, out var buckets)
                    || buckets.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                foreach (var bucket in buckets.EnumerateObject())
                {
                    if (NormalizeBucket(bucket.Name) != "notimplemented"
                        || bucket.Value.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in bucket.Value.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object
                            || !entry.TryGetProperty("esriPath", out var path))
                        {
                            continue;
                        }

                        var value = path.GetString();
                        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith('/'))
                        {
                            continue;
                        }

                        var name = entry.TryGetProperty("name", out var n) ? n.GetString() ?? "(unnamed)" : "(unnamed)";
                        yield return (serviceId, name, value);
                    }
                }
            }
        }
    }

    /// <summary>Folds <c>notImplemented</c>/<c>not_implemented</c> onto one comparable token.</summary>
    private static string NormalizeBucket(string bucket)
        => bucket.Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();

    /// <summary>Strips route constraints so matrix routes compare against registry routes.</summary>
    private static string NormalizeRoute(string route)
        => RouteConstraintRegex.Replace(route.Trim(), "{${name}}");

    private static string CommittedArtifactPath()
    {
        var path = ArchitectureTestHelpers.CombinePath(
            ArchitectureTestHelpers.ResolveRepositoryRoot(), "docs", "gis", "data", "geoservices-rest-parity.json");
        File.Exists(path).Should().BeTrue($"the published parity matrix must exist at {RelativePath}");
        return path;
    }
}
