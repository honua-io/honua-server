// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Generalized cross-protocol isolation guardrail.
///
/// <para>
/// Per AGENTS.md "Protocol Adapter Architecture": no protocol adapter may depend on
/// another protocol adapter's implementation. Shared behavior must be extracted into a
/// neutral Core/Infrastructure service that both protocols adapt to. Previously only the
/// single pair <c>Ogc.Classic -&gt; GeoServices</c> was guarded (see
/// <see cref="DependencyRulesTests.ClassicOgcProtocols_ShouldNotDependOn_GeoServicesProtocols"/>),
/// leaving 12 of 14 adapter families unchecked. This test asserts the full matrix:
/// source under <c>Features/Protocols/{Family}</c> must not reference any <em>other</em>
/// protocol family's namespace, except for the explicitly documented allow-list below.
/// </para>
///
/// <para>
/// Detection is source-based (scanning the namespace token
/// <c>Honua.Server.Features.Protocols.{Family}</c> in each <c>.cs</c> file) rather than
/// reflection-based, because protocol adapters frequently couple to each other only through
/// static helper calls in method bodies (for example
/// <c>GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(...)</c>), which a reflection
/// member-signature walk cannot observe. Source scanning catches both <c>using</c> directives
/// and fully-qualified references, and matches how the tech-debt file counts below were taken.
/// </para>
///
/// <para>
/// The allow-list is a tech-debt ratchet: it pins the cross-family couplings that exist
/// today so the test passes against current code, while failing the moment a NEW
/// cross-family leak is introduced. Entries must be burned down, not grown.
/// </para>
/// </summary>
[Trait("Category", "Architecture")]
public sealed class CrossProtocolIsolationTests
{
    private const string ProtocolsRootNamespace = "Honua.Server.Features.Protocols";

    /// <summary>
    /// The 14 protocol adapter families under <c>Features/Protocols/</c>. Each value is the
    /// directory/namespace segment immediately following <c>Protocols.</c>; sub-namespaces
    /// (for example <c>Ogc.Api</c>, <c>Ogc.Classic</c>, <c>Ogc.Common</c>) collapse into their
    /// family, so references between them are treated as same-family (allowed) and only
    /// cross-FAMILY leaks are flagged.
    /// </summary>
    private static readonly string[] _protocolFamilies =
    {
        "Cog",
        "Coverages",
        "Elevation",
        "GeoServices",
        "Grpc",
        "Mcp",
        "OData",
        "Ogc",
        "Scene",
        "SpatialAnalytics",
        "Stac",
        "Terrain",
        "Tiles",
        "Zarr"
    };

    /// <summary>
    /// Tech-debt allow-list of tolerated cross-family protocol couplings. Each entry pins a
    /// (sourceFamily, targetFamily, maxFileCount) triple: source files under
    /// <c>Features/Protocols/{sourceFamily}</c> are permitted to reference
    /// <c>{ProtocolsRootNamespace}.{targetFamily}</c> as long as the number of such files does
    /// not exceed <c>maxFileCount</c>.
    ///
    /// <para>
    /// EVERY ENTRY HERE IS TECH DEBT TO BE REMOVED. Each represents a protocol adapter that
    /// reaches into another adapter instead of a neutral Core/Infrastructure service. When a
    /// coupling shrinks, decrement <c>MaxFileCount</c> in the same change; when it reaches zero,
    /// delete the entry. Do not add new entries to unblock a feature — extract a shared service
    /// instead.
    /// </para>
    ///
    /// <para>
    /// The ratchet is enforced by <see cref="ProtocolFamilies_ShouldNotDependOn_OtherProtocolFamilies"/>:
    /// the test fails if the actual file count for an allowed pair either EXCEEDS
    /// <c>MaxFileCount</c> (regression) or is LOWER than <c>MaxFileCount</c> (debt was paid
    /// down — decrement the cap so the ratchet tightens).
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>SpatialAnalytics -&gt; GeoServices</c> (cap = 5): the SpatialAnalytics request
    /// handlers call <c>GeoServicesRequestValueHelpers</c> / <c>GeoServicesGeometryConverter</c>
    /// directly instead of a transport-neutral analytics request surface. Files (5):
    /// <c>SpatialAnalyticsRequestHandlers.cs</c>,
    /// <c>SpatialAnalyticsRequestHandlers.BufferAggregate.cs</c>,
    /// <c>SpatialAnalyticsRequestHandlers.Clusters.cs</c>,
    /// <c>SpatialAnalyticsRequestHandlers.Density.cs</c>,
    /// <c>SpatialAnalyticsRequestHandlers.SpatialJoin.cs</c>.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>Stac -&gt; Ogc</c> (cap = 7): the STAC endpoints/models/JSON context reuse OGC API
    /// adapter types (collection/feature DTOs, link/conformance models, OGC JSON contexts)
    /// directly instead of a shared catalog/metadata service. Files (7):
    /// <c>CatalogEndpoints.cs</c>, <c>CollectionEndpoints.cs</c>, <c>ItemEndpoints.cs</c>,
    /// <c>SearchEndpoints.cs</c>, <c>StacJsonContext.cs</c>, <c>Models/StacModels.cs</c>,
    /// <c>Services/StacMappingService.cs</c>.
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlyList<CrossProtocolAllowance> _allowedCrossProtocolRefs =
        new CrossProtocolAllowance[]
        {
            // Tech debt — burn down when SpatialAnalytics adapts to a neutral analytics service.
            new("SpatialAnalytics", "GeoServices", MaxFileCount: 5),

            // Tech debt — burn down when STAC adapts to a shared catalog/metadata service.
            new("Stac", "Ogc", MaxFileCount: 7),
        };

    /// <summary>
    /// A single ratcheted cross-family allowance: source family <paramref name="From"/> may
    /// reference target family <paramref name="To"/> from at most <paramref name="MaxFileCount"/>
    /// files. The cap is the ratchet — it can only shrink.
    /// </summary>
    private sealed record CrossProtocolAllowance(string From, string To, int MaxFileCount);

    /// <summary>
    /// Source roots for protocol families that have been physically extracted into their own
    /// <c>Honua.Protocols.&lt;X&gt;</c> assembly. Their namespace is preserved
    /// (<c>Honua.Server.Features.Protocols.{Family}</c>) so the cross-family matchers still
    /// work, but their source no longer lives under <c>Honua.Server/Features/Protocols</c>.
    /// Each value is a repo-relative directory; unextracted families default to the Server
    /// path. As more families are extracted, add their project root here.
    /// </summary>
    private static readonly Dictionary<string, string> _extractedFamilyRoots =
        new(StringComparer.Ordinal)
        {
            ["OData"] = Path.Combine("src", "Honua.Protocols.OData"),
            // MCP is the AI module's protocol surface (consolidated into Honua.Ai
            // rather than a standalone Honua.Protocols.Mcp, because Ai and Mcp are
            // mutually dependent — MCP tools delegate to AiBuilder/Grounding).
            ["Mcp"] = Path.Combine("src", "Honua.Ai", "Features", "Protocols", "Mcp"),
        };

    private static string[] EnumerateFamilySource(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                .Where(path => !IsBuildArtifact(path))
                .ToArray()
            : Array.Empty<string>();

    private static bool IsBuildArtifact(string path)
    {
        var sep = Path.DirectorySeparatorChar;
        return path.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
            || path.Contains($"{sep}obj{sep}", StringComparison.Ordinal);
    }

    [ArchitectureTest]
    public void ProtocolFamilies_ShouldNotDependOn_OtherProtocolFamilies()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var protocolsPath = Path.Combine(
            repositoryRoot, "src", "Honua.Server", "Features", "Protocols");

        Directory.Exists(protocolsPath).Should().BeTrue(
            $"protocol adapters must live under {protocolsPath}");

        // Pre-compiled matchers for each family's namespace, e.g. a reference to
        // "Honua.Server.Features.Protocols.GeoServices" or any sub-namespace thereof.
        var familyMatchers = _protocolFamilies.ToDictionary(
            family => family,
            family => new Regex(
                $@"\b{Regex.Escape(ProtocolsRootNamespace)}\.{Regex.Escape(family)}(?=[\s.;<>(){{}}]|$)",
                RegexOptions.Compiled),
            StringComparer.Ordinal);

        var filesByFamily = _protocolFamilies.ToDictionary(
            family => family,
            family => EnumerateFamilySource(
                _extractedFamilyRoots.TryGetValue(family, out var extractedRoot)
                    ? Path.Combine(repositoryRoot, extractedRoot)
                    : Path.Combine(protocolsPath, family)),
            StringComparer.Ordinal);

        // Sanity check: every guarded family must actually have source, otherwise a rename
        // would silently disable the guardrail.
        var missingFamilies = _protocolFamilies
            .Where(family => filesByFamily[family].Length == 0)
            .ToArray();
        missingFamilies.Should().BeEmpty(
            "every guarded protocol family must resolve to source files; a missing family " +
            "means a rename has silently disabled the cross-protocol guardrail. " +
            $"Missing: {string.Join(", ", missingFamilies)}");

        // Index allowances by (from, to) for O(1) lookup, and validate the table itself: every
        // allowance must reference known families, and no pair may be listed twice.
        var allowanceIndex = new Dictionary<(string From, string To), CrossProtocolAllowance>();
        var familySet = new HashSet<string>(_protocolFamilies, StringComparer.Ordinal);
        foreach (var allowance in _allowedCrossProtocolRefs)
        {
            familySet.Should().Contain(allowance.From,
                $"allow-list source family '{allowance.From}' must be a known protocol family");
            familySet.Should().Contain(allowance.To,
                $"allow-list target family '{allowance.To}' must be a known protocol family");
            allowance.From.Should().NotBe(allowance.To,
                "self-references are same-family and never need an allow-list entry");
            allowance.MaxFileCount.Should().BeGreaterThan(0,
                $"allowance '{allowance.From} -> {allowance.To}' must allow at least one file; " +
                "remove the entry instead of setting MaxFileCount to zero");

            allowanceIndex.ContainsKey((allowance.From, allowance.To)).Should().BeFalse(
                $"duplicate allow-list entry for '{allowance.From} -> {allowance.To}'");
            allowanceIndex[(allowance.From, allowance.To)] = allowance;
        }

        var violations = new List<string>();
        // (from, to) -> actual count of files in `from` that reference `to`. We bucket so we can
        // both report unexpected violations and enforce the ratchet against the allow-list caps.
        var actualCounts = new Dictionary<(string From, string To), int>();

        foreach (var family in _protocolFamilies)
        {
            foreach (var file in filesByFamily[family])
            {
                var contents = File.ReadAllText(file);

                foreach (var otherFamily in _protocolFamilies)
                {
                    if (otherFamily == family)
                    {
                        continue;
                    }

                    if (!familyMatchers[otherFamily].IsMatch(contents))
                    {
                        continue;
                    }

                    var key = (From: family, To: otherFamily);
                    actualCounts.TryGetValue(key, out var prior);
                    actualCounts[key] = prior + 1;

                    if (allowanceIndex.ContainsKey(key))
                    {
                        // Tolerated for now; ratchet enforcement happens below against the cap.
                        continue;
                    }

                    var relative = Path.GetRelativePath(repositoryRoot, file);
                    violations.Add(
                        $"Protocol family '{family}' file '{relative}' references protocol " +
                        $"family '{otherFamily}' ('{ProtocolsRootNamespace}.{otherFamily}'). " +
                        "Protocol adapters must not depend on each other; extract a neutral " +
                        "Core/Infrastructure service that both adapt to.");
                }
            }
        }

        violations
            .Distinct(StringComparer.Ordinal)
            .OrderBy(message => message, StringComparer.Ordinal)
            .Should()
            .BeEmpty(
                "cross-family protocol coupling must go through shared canonical pipelines. " +
                "If a coupling is intentional tech debt, pin it in the allow-list with a " +
                "file-count cap and a burn-down note.");

        // ----- Ratchet enforcement -----
        // For each allow-list entry, the actual file count must MATCH the declared cap exactly.
        // - Actual > cap: regression — a new file in this family started leaking into the target.
        //   Either remove the new coupling, or (only if truly justified) raise the cap with a
        //   reviewed comment update.
        // - Actual < cap: debt was paid down — decrement the cap in this same change so the
        //   ratchet keeps tightening and can never silently regress back up to the old value.
        // This combination makes the allow-list a one-way ratchet: caps can only shrink.
        var ratchetFailures = new List<string>();
        foreach (var allowance in _allowedCrossProtocolRefs)
        {
            actualCounts.TryGetValue((allowance.From, allowance.To), out var actual);

            if (actual > allowance.MaxFileCount)
            {
                ratchetFailures.Add(
                    $"Allow-list cap for '{allowance.From} -> {allowance.To}' exceeded: " +
                    $"declared MaxFileCount = {allowance.MaxFileCount}, actual = {actual}. " +
                    "A new cross-family coupling was introduced. Remove the new reference, or " +
                    "(only if justified) raise the cap and update the file list in the comment.");
            }
            else if (actual < allowance.MaxFileCount)
            {
                ratchetFailures.Add(
                    $"Allow-list cap for '{allowance.From} -> {allowance.To}' is loose: " +
                    $"declared MaxFileCount = {allowance.MaxFileCount}, actual = {actual}. " +
                    "Tech debt was paid down — decrement MaxFileCount to the actual count " +
                    "(or delete the entry if actual is 0) so the ratchet tightens.");
            }
        }

        ratchetFailures
            .OrderBy(message => message, StringComparer.Ordinal)
            .Should()
            .BeEmpty(
                "the cross-protocol allow-list is a one-way ratchet: caps can only shrink, never " +
                "grow. Tightening keeps the guardrail honest as debt is repaid.");
    }
}
