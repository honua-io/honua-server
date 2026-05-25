// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Server;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Drift guard for the audit finding (#1144): <see cref="EndpointRegistry"/> is a
/// hand-curated catalog of every HTTP endpoint exposed by Honua.Server, and the
/// reviewer flagged it as fragile against drift. This test scans the
/// minimal-API <c>Map{Get,Post,Put,Delete,Patch}</c> calls under
/// <c>src/Honua.Server/Features</c> and asserts that every literal route it
/// finds is also present in <see cref="EndpointRegistry.All"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scanning is intentionally text-based (regex over the source files) so the
/// test does not pull in Roslyn workspace dependencies. It resolves
/// <c>MapGroup</c> prefix chains for receivers assigned in the same file,
/// normalises route constraints (<c>{id:guid}</c> → <c>{id}</c>) and the API
/// versioning placeholder (<c>v{version:apiVersion}</c> → <c>v1</c>).
/// </para>
/// <para>
/// Two cases are out of scope and are filtered before the comparison: (a)
/// routes mapped via <c>MapMethods(...)</c> rather than the typed verb helpers
/// (the spec explicitly names only the five verbs); and (b) routes whose
/// receiver is a method parameter so the group prefix is not statically
/// resolvable from a single file. Test-only probe routes
/// (<c>/__test/...</c>) are also excluded — they only mount in the Test
/// environment and would otherwise pollute the catalog.
/// </para>
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class EndpointRegistryDriftTests
{
    private const string FeaturesRelativePath = "src/Honua.Server/Features";

    private static readonly Regex MapVerbRegex = new(
        @"\.(?<verb>MapGet|MapPost|MapPut|MapDelete|MapPatch)\s*\(\s*(?:""(?<route>[^""]*)""|@""(?<route2>[^""]*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MapGroupAssignRegex = new(
        @"(?:var\s+)?(?<var>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*(?:""(?<prefix>[^""]*)""|@""(?<prefix2>[^""]*)"")",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPlaceholderRegex = new(
        @"v\{version(?::[^{}]+)?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RouteConstraintRegex = new(
        @"\{(?<name>[^{}:]+):[^{}]+\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Path prefixes considered "resolvable enough" to enforce. Anything that
    /// does not start with one of these is assumed to be a fragment from a
    /// helper method that takes the route group as a parameter; we cannot
    /// statically resolve those without crossing method boundaries.
    /// </summary>
    private static readonly string[] EnforcedPrefixes =
    {
        "/api/",
        "/rest/",
        "/odata",
        "/ogc/",
        "/stac",
        "/scenes/",
        "/static/",
        "/elevation/",
        "/tiles/",
        "/terrain/",
        "/healthz/",
        "/metrics",
        "/monitoring/",
        "/oauth/",
        "/csp-violation-report",
        "/samples/",
        "/docs",
        "/v1/",
        "/mcp",
        "/wfs",
        "/open-data",
        "/openapi.json",
    };

    /// <summary>
    /// Routes scanned from source files that are intentionally excluded from
    /// the registry (test-only probes, etc.).
    /// </summary>
    private static readonly HashSet<(string Method, string Path)> SourceExclusions = new()
    {
        ("GET", "/__test/cross-server-consume/proxy"),
    };

    /// <summary>
    /// Registry entries whose mapping intentionally lives outside the
    /// features tree (Program.cs, Startup helpers, third-party middleware).
    /// Adding to this set must come with a one-line comment justifying why
    /// the route cannot be anchored to a feature file.
    /// </summary>
    private static readonly HashSet<(string Method, string Path)> KnownNonFeatureRegistryEntries = new()
    {
        // Hosted sample redirect wired in Program.cs.
        ("GET", "/samples/stac-ops"),
        // Prometheus exporter mounted by ServiceDefaults.
        ("GET", "/metrics"),
        // Scalar interactive API explorer wired in Program.cs.
        ("GET", "/docs"),
    };

    [ArchitectureTest]
    public void EveryDiscoveredEndpoint_IsRegisteredInEndpointRegistry()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);
        Directory.Exists(featuresRoot)
            .Should().BeTrue($"the audited features directory should exist at {featuresRoot}");

        var discovered = ScanFeatureEndpoints(featuresRoot, repoRoot);
        var registered = BuildRegistrySet();

        var enforceable = discovered
            .Where(d => StartsWithEnforcedPrefix(d.Path))
            .Where(d => !SourceExclusions.Contains((d.Method, d.Path)))
            .ToList();

        var missing = enforceable
            .Where(d => !registered.Contains((d.Method, d.Path)))
            .Select(d => $"{d.Method} {d.Path}  (declared in {d.SourceFile}:{d.Line})")
            .OrderBy(s => s, System.StringComparer.Ordinal)
            .ToList();

        missing.Should().BeEmpty(
            "every Map{Get,Post,Put,Delete,Patch} route declared under "
            + FeaturesRelativePath
            + " must also appear in EndpointRegistry.All; add the missing entries to keep the "
            + "registry and the route table in sync. If a route really should not be catalogued "
            + "(e.g. a test-only probe), extend the SourceExclusions set in this test with a "
            + "justification comment.");
    }

    [ArchitectureTest]
    public void EveryRegistryEntry_RetainsAMappedSource()
    {
        // The reverse-direction check is only as strict as our scanner can
        // afford: a path is considered anchored if any of its segments,
        // combined with a recognised MapGroup prefix elsewhere in the
        // features tree, appears as a literal route in source. The check is
        // deliberately permissive — many endpoints are mapped via MapMethods,
        // MapGroup helper methods that take a route group as a parameter, or
        // interpolated path constants the verb-scan cannot trace
        // cross-method. The strict forward direction
        // (<see cref="EveryDiscoveredEndpoint_IsRegisteredInEndpointRegistry"/>)
        // is what catches accidental additions; this test catches the
        // simpler case where someone deletes the mapping but forgets the
        // catalog entry.
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);
        var literalRoutes = CollectLiteralRouteFragments(featuresRoot);

        var orphans = BuildRegistrySet()
            .Where(entry => !KnownNonFeatureRegistryEntries.Contains(entry))
            .Where(entry => !IsAnchoredInFeatures(entry.Path, literalRoutes))
            .Select(entry => $"{entry.Method} {entry.Path}")
            .OrderBy(s => s, System.StringComparer.Ordinal)
            .ToList();

        orphans.Should().BeEmpty(
            "EndpointRegistry.All declares the listed entries but no Map-style call under "
            + FeaturesRelativePath + " references the leaf route fragment; if the route is "
            + "wired outside the features tree (e.g. Program.cs or a Startup helper), add it "
            + "to " + nameof(KnownNonFeatureRegistryEntries) + " with a justification.");
    }

    [ArchitectureTest]
    public void FeatureScan_FindsASubstantialNumberOfEndpoints()
    {
        // Guards against a regression where the regex or directory layout
        // changes and the scan silently passes because it discovered nothing.
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var featuresRoot = Path.Combine(repoRoot, FeaturesRelativePath);

        var discovered = ScanFeatureEndpoints(featuresRoot, repoRoot);

        discovered.Count.Should().BeGreaterThan(200,
            "the scanner should detect well over two hundred Map{Get,Post,...} calls; "
            + "a count this low usually means the scanner stopped matching the source layout.");
    }

    private static HashSet<(string Method, string Path)> BuildRegistrySet()
        => EndpointRegistry.All
            .Select(entry => (entry.Method.ToUpperInvariant(), NormalizeRoute(entry.Path)))
            .ToHashSet();

    private static bool StartsWithEnforcedPrefix(string path)
        => EnforcedPrefixes.Any(prefix => path.StartsWith(prefix, System.StringComparison.Ordinal));

    private static LiteralRouteIndex CollectLiteralRouteFragments(string featuresRoot)
    {
        // Build several views of every route fragment we can find in the
        // features tree:
        //   * fullRoutes      — literal route strings passed to Map / MapGet /
        //                       MapPost / MapPut / MapDelete / MapPatch /
        //                       MapMethods / MapGroup. Also includes the
        //                       static portion of interpolated strings.
        //   * groupPrefixes   — literal route strings passed specifically to
        //                       MapGroup.
        //   * combinedText    — raw concatenated source (comments stripped)
        //                       so we can do a final substring fallback for
        //                       routes mapped via attribute-style helpers or
        //                       const path bases.
        var fullRoutes = new HashSet<string>(System.StringComparer.Ordinal);
        var groupPrefixes = new HashSet<string>(System.StringComparer.Ordinal);
        var combinedText = new System.Text.StringBuilder();

        var anyMapCall = new Regex(
            @"\.(?<kind>Map[A-Za-z]*)\s*\(\s*(?:""(?<route>[^""]*)""|@""(?<route2>[^""]*)""|\$""(?<route3>[^""]*)"")",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Direction-2 anchoring uses the broader features tree (route paths
        // can live in companion files such as *Utilities.cs constants).
        foreach (var file in Directory.EnumerateFiles(featuresRoot, "*.cs", SearchOption.AllDirectories))
        {
            combinedText.Append(StripComments(File.ReadAllText(file)));
            combinedText.Append('\n');
        }

        foreach (var file in EnumerateEndpointFiles(featuresRoot))
        {
            var content = StripComments(File.ReadAllText(file));

            foreach (Match match in anyMapCall.Matches(content))
            {
                var kind = match.Groups["kind"].Value;
                string raw;
                if (match.Groups["route"].Success)
                {
                    raw = match.Groups["route"].Value;
                }
                else if (match.Groups["route2"].Success)
                {
                    raw = match.Groups["route2"].Value;
                }
                else
                {
                    raw = match.Groups["route3"].Value;
                    // Interpolated strings escape literal braces as `{{` / `}}`.
                    raw = raw.Replace("{{", "{", System.StringComparison.Ordinal)
                             .Replace("}}", "}", System.StringComparison.Ordinal);
                }
                var normalised = NormalizeRoute(raw);

                fullRoutes.Add(normalised);
                if (string.Equals(kind, "MapGroup", System.StringComparison.Ordinal))
                {
                    groupPrefixes.Add(normalised);
                }
            }
        }

        return new LiteralRouteIndex(fullRoutes, groupPrefixes, combinedText.ToString());
    }

    private static bool IsAnchoredInFeatures(string normalisedRegistryPath, LiteralRouteIndex index)
    {
        if (index.FullRoutes.Contains(normalisedRegistryPath))
        {
            return true;
        }

        // (prefix, suffix) split across MapGroup + Map*.
        foreach (var prefix in index.GroupPrefixes)
        {
            if (!normalisedRegistryPath.StartsWith(prefix, System.StringComparison.Ordinal))
            {
                continue;
            }

            var suffix = normalisedRegistryPath[prefix.Length..];
            if (suffix.Length == 0)
            {
                return true;
            }

            if (index.FullRoutes.Contains(suffix))
            {
                return true;
            }
        }

        // Fallback for routes whose template lives in a const + interpolated
        // string ($"{BasePath}/jobs/{{jobId}}"): scan the raw combined source
        // for the suffix following the deepest matching group prefix or for
        // the registry path's own trailing segment.
        if (HasInterpolatedTemplateMatch(normalisedRegistryPath, index))
        {
            return true;
        }

        return false;
    }

    private static bool HasInterpolatedTemplateMatch(string registryPath, LiteralRouteIndex index)
    {
        // Find the longest base prefix that appears anywhere in the source.
        // Then verify the remainder of the registry path (templated as an
        // interpolated string, where literal braces become `{{...}}`) appears
        // in the combined source.
        for (var slashIndex = registryPath.Length; slashIndex > 0; slashIndex--)
        {
            if (slashIndex < registryPath.Length && registryPath[slashIndex] != '/')
            {
                continue;
            }

            var head = registryPath[..slashIndex];
            var tail = registryPath[slashIndex..];

            if (!index.CombinedSource.Contains(head, System.StringComparison.Ordinal))
            {
                continue;
            }

            if (tail.Length == 0)
            {
                return true;
            }

            // Interpolated form: `{` becomes `{{` and `}` becomes `}}`.
            var interpolated = tail.Replace("{", "{{", System.StringComparison.Ordinal)
                                   .Replace("}", "}}", System.StringComparison.Ordinal);

            if (index.CombinedSource.Contains(tail, System.StringComparison.Ordinal)
                || index.CombinedSource.Contains(interpolated, System.StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record LiteralRouteIndex(
        HashSet<string> FullRoutes,
        HashSet<string> GroupPrefixes,
        string CombinedSource);

    private static List<DiscoveredEndpoint> ScanFeatureEndpoints(string featuresRoot, string repoRoot)
    {
        var discovered = new List<DiscoveredEndpoint>();
        var hostWiringSource = LoadHostWiringSource(repoRoot);

        foreach (var file in EnumerateEndpointFiles(featuresRoot))
        {
            var raw = File.ReadAllText(file);
            var content = StripComments(raw);
            if (!IsFileWiredFromHost(content, hostWiringSource))
            {
                // The endpoints declared here are not reachable through any
                // host startup path (Program.cs / Startup helpers). They are
                // either provisional or behind a feature flag; either way
                // they have no registry obligation.
                continue;
            }

            var groupPrefixes = ResolveGroupPrefixes(content);

            foreach (Match match in MapVerbRegex.Matches(content))
            {
                var route = match.Groups["route"].Success
                    ? match.Groups["route"].Value
                    : match.Groups["route2"].Value;
                var verb = match.Groups["verb"].Value.Substring("Map".Length).ToUpperInvariant();

                var receiver = ExtractReceiverBefore(content, match.Index);
                var prefix = receiver is not null && groupPrefixes.TryGetValue(receiver, out var p)
                    ? p
                    : string.Empty;

                var combined = NormalizeRoute(prefix + route);
                var lineNumber = raw[..match.Index].Count(c => c == '\n') + 1;

                discovered.Add(new DiscoveredEndpoint(
                    verb,
                    combined,
                    Path.GetRelativePath(repoRoot, file),
                    lineNumber));
            }
        }

        return discovered;
    }

    private static string LoadHostWiringSource(string repoRoot)
    {
        // Slurp every Honua.Server source file *except* the file being
        // examined, so the drift scan can answer "is this Map*Endpoints()
        // helper actually called from anywhere else?". Endpoint helpers can
        // be invoked from Program.cs, Startup helpers, or sibling feature
        // dispatcher files (e.g. CollaborationEndpoints chaining
        // MapCollaborationSessionEndpoints), so all server sources are in
        // scope.
        var sb = new System.Text.StringBuilder();
        var serverRoot = Path.Combine(repoRoot, "src", "Honua.Server");
        foreach (var file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            sb.Append("// FILE:").Append(file).Append('\n');
            sb.Append(StripComments(File.ReadAllText(file)));
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool IsFileWiredFromHost(string fileContent, string hostWiring)
    {
        // Every endpoint file exposes one or more `public static …
        // Map<Whatever>(this IEndpointRouteBuilder|WebApplication …)`
        // extension methods. The file is considered wired iff at least one
        // helper is referenced from elsewhere in the host tree.
        var publicMapMethods = Regex.Matches(
            fileContent,
            @"public\s+static\s+\S+\s+(?<name>Map\w+)\s*\(\s*this\b",
            RegexOptions.CultureInvariant);

        if (publicMapMethods.Count == 0)
        {
            // No public Map*( ) helper. Treat as wired to avoid silently
            // dropping endpoints declared via attribute routing or some
            // other indirect mechanism.
            return true;
        }

        foreach (Match match in publicMapMethods)
        {
            var name = match.Groups["name"].Value;
            // Count call sites — the declaration adds exactly one occurrence
            // because the host wiring slurp also contains this file. Anything
            // above one indicates a real call elsewhere.
            var pattern = new Regex(
                @"(?:^|[^\w])" + Regex.Escape(name) + @"\s*\(",
                RegexOptions.CultureInvariant);
            if (pattern.Count(hostWiring) >= 2)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> EnumerateEndpointFiles(string featuresRoot)
    {
        // *Endpoints.cs covers the canonical pattern; *Endpoints.*.cs picks up
        // partial-class continuations such as FeatureServerEndpoints.Query.cs.
        return Directory.EnumerateFiles(featuresRoot, "*Endpoints.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(featuresRoot, "*Endpoints.*.cs", SearchOption.AllDirectories))
            .Distinct(System.StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ResolveGroupPrefixes(string content)
    {
        // Build receiver-variable → resolved prefix. The walk is iterative so
        // chained groups (`var inner = outer.MapGroup("/x")`) resolve once
        // their parent is known.
        var raw = new List<(string Var, string Parent, string Prefix)>();
        foreach (Match match in MapGroupAssignRegex.Matches(content))
        {
            var variable = match.Groups["var"].Value;
            var parent = match.Groups["parent"].Value;
            var prefix = match.Groups["prefix"].Success
                ? match.Groups["prefix"].Value
                : match.Groups["prefix2"].Value;
            raw.Add((variable, parent, prefix));
        }

        var resolved = new Dictionary<string, string>(System.StringComparer.Ordinal);
        for (var iteration = 0; iteration < raw.Count + 1; iteration++)
        {
            var madeProgress = false;
            foreach (var (variable, parent, prefix) in raw)
            {
                if (resolved.ContainsKey(variable))
                {
                    continue;
                }

                if (resolved.TryGetValue(parent, out var parentPrefix))
                {
                    resolved[variable] = parentPrefix + prefix;
                    madeProgress = true;
                }
                else
                {
                    // Parent isn't itself a MapGroup receiver in this file — it
                    // is likely the root endpoints builder (app/endpoints), so
                    // treat the prefix as absolute.
                    resolved[variable] = prefix;
                    madeProgress = true;
                }
            }

            if (!madeProgress)
            {
                break;
            }
        }

        return resolved;
    }

    private static string? ExtractReceiverBefore(string content, int matchIndex)
    {
        var slice = content[..matchIndex];
        var trailing = Regex.Match(slice, @"(?<name>\w+)\s*$", RegexOptions.CultureInvariant);
        return trailing.Success ? trailing.Groups["name"].Value : null;
    }

    private static string NormalizeRoute(string route)
    {
        if (string.IsNullOrEmpty(route))
        {
            return route;
        }

        var normalised = VersionPlaceholderRegex.Replace(route, "v1");
        normalised = RouteConstraintRegex.Replace(normalised, "{${name}}");
        normalised = Regex.Replace(normalised, "//+", "/");

        if (normalised.Length > 1 && normalised.EndsWith('/'))
        {
            normalised = normalised.TrimEnd('/');
        }

        return normalised;
    }

    private static string StripComments(string source)
    {
        var noBlockComments = Regex.Replace(source, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline);
        return Regex.Replace(noBlockComments, "//[^\n]*", string.Empty);
    }

    private sealed record DiscoveredEndpoint(string Method, string Path, string SourceFile, int Line);
}
