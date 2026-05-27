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
    /// Tech-debt allow-list of tolerated cross-family protocol couplings, keyed by the
    /// depending family with the set of families it is (for now) permitted to reference.
    ///
    /// <para>
    /// EVERY ENTRY HERE IS TECH DEBT TO BE REMOVED. Each represents a protocol adapter that
    /// reaches into another adapter instead of a neutral Core/Infrastructure service. When the
    /// coupling is refactored away, delete the entry so the ratchet tightens. Do not add new
    /// entries to unblock a feature — extract a shared service instead.
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <c>SpatialAnalytics -&gt; GeoServices</c>: the SpatialAnalytics request handlers
    /// (buffer/aggregate, spatial-join, density, clusters, and the shared base handler —
    /// 5 files as of this commit) call GeoServices adapter helpers directly instead of a
    /// transport-neutral analytics request surface.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <c>Stac -&gt; Ogc</c>: the STAC catalog/collection/item/search endpoints, mapping
    /// service, JSON context, and models (7 files as of this commit) reuse OGC API adapter
    /// types directly instead of a shared catalog/metadata service.
    /// </description>
    /// </item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, IReadOnlyCollection<string>> _allowedCrossProtocolRefs =
        new(StringComparer.Ordinal)
        {
            // Tech debt — remove when SpatialAnalytics adapts to a neutral analytics service (~5 files).
            ["SpatialAnalytics"] = new[] { "GeoServices" },

            // Tech debt — remove when STAC adapts to a shared catalog/metadata service (~7 files).
            ["Stac"] = new[] { "Ogc" }
        };

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
            family => Directory.Exists(Path.Combine(protocolsPath, family))
                ? Directory.EnumerateFiles(Path.Combine(protocolsPath, family), "*.cs", SearchOption.AllDirectories).ToArray()
                : Array.Empty<string>(),
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

        var violations = new List<string>();

        foreach (var family in _protocolFamilies)
        {
            var allowed = _allowedCrossProtocolRefs.TryGetValue(family, out var permitted)
                ? permitted
                : Array.Empty<string>();

            foreach (var file in filesByFamily[family])
            {
                var contents = File.ReadAllText(file);

                foreach (var otherFamily in _protocolFamilies)
                {
                    if (otherFamily == family || allowed.Contains(otherFamily, StringComparer.Ordinal))
                    {
                        continue;
                    }

                    if (familyMatchers[otherFamily].IsMatch(contents))
                    {
                        var relative = Path.GetRelativePath(repositoryRoot, file);
                        violations.Add(
                            $"Protocol family '{family}' file '{relative}' references protocol " +
                            $"family '{otherFamily}' ('{ProtocolsRootNamespace}.{otherFamily}'). " +
                            "Protocol adapters must not depend on each other; extract a neutral " +
                            "Core/Infrastructure service that both adapt to.");
                    }
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
                "file-count comment and a burn-down note.");
    }
}
