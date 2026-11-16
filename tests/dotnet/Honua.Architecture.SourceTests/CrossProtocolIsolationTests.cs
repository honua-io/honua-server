// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class CrossProtocolIsolationTests
{
    private const string ProtocolsRootNamespace = "Honua.Server.Features.Protocols";

    private static string FamilyNamespace(string family) => family switch
    {
        "OData" => "Honua.Protocols.OData",
        "Scene" => "Honua.Protocols.Scene",
        "Stac" => "Honua.Protocols.Stac",
        "SensorThings" => "Honua.Protocols.SensorThings",
        "GeoServices" => "Honua.Protocols.GeoServices",
        "Ogc" => "Honua.Protocols.Ogc",
        "Mcp" => "Honua.Ai.Protocols.Mcp",
        _ => $"{ProtocolsRootNamespace}.{family}",
    };

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
        "SensorThings",
        "SpatialAnalytics",
        "Stac",
        "Terrain",
        "Tiles",
        "Zarr"
    };

    private static readonly IReadOnlyList<CrossProtocolAllowance> _allowedCrossProtocolRefs =
        new CrossProtocolAllowance[]
        {
            // Tech debt — burn down when SpatialAnalytics adapts to a neutral analytics service.
            new("SpatialAnalytics", "GeoServices", MaxFileCount: 5),

            // Tech debt — burn down when STAC adapts to a shared catalog/metadata service.
            new("Stac", "Ogc", MaxFileCount: 7),
        };

    private sealed record CrossProtocolAllowance(string From, string To, int MaxFileCount);

    private static readonly Dictionary<string, string[]> _extractedFamilyRoots =
        new(StringComparer.Ordinal)
        {
            ["OData"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "OData") },
            ["Scene"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "Scene") },
            ["Stac"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "Stac") },
            ["SensorThings"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "SensorThings") },
            ["GeoServices"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "GeoServices") },
            // MCP is the AI module's protocol surface (consolidated into Honua.Ai
            // rather than a standalone Honua.Protocols.Mcp, because Ai and Mcp are
            // mutually dependent — MCP tools delegate to AiBuilder/Grounding).
            ["Mcp"] = new[] { ArchitectureTestHelpers.CombinePath("src", "Honua.Ai", "Features", "Protocols", "Mcp") },
            // Ogc spans the extracted OgcApi + OgcClassic assemblies + the shared
            // Ogc.Shared foundation (the Server Ogc/ folder is now fully extracted).
            // They collapse into one "Ogc" family, so OgcClassic -> OgcApi is an
            // allowed intra-family reference.
            ["Ogc"] = new[]
            {
                ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "OgcApi"),
                ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "OgcClassic"),
                ArchitectureTestHelpers.CombinePath("src", "Honua.Protocols", "Ogc.Shared"),
            },
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
        var protocolsPath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot, "src", "Honua.Server", "Features", "Protocols");

        Directory.Exists(protocolsPath).Should().BeTrue(
            $"protocol adapters must live under {protocolsPath}");

        // Pre-compiled matchers for each family's namespace, e.g. a reference to
        // "Honua.Protocols.GeoServices" or any sub-namespace thereof.
        var familyMatchers = _protocolFamilies.ToDictionary(
            family => family,
            family => new Regex(
                $@"\b{Regex.Escape(FamilyNamespace(family))}(?=[\s.;<>(){{}}]|$)",
                RegexOptions.Compiled),
            StringComparer.Ordinal);

        var filesByFamily = _protocolFamilies.ToDictionary(
            family => family,
            family =>
            {
                // Always scan the in-Server path, then union any extracted assembly
                // roots. A family can legitimately span several locations (e.g. Ogc =
                // Server Ogc/Classic + Honua.Protocols.OgcApi + Honua.Protocols.Ogc.Shared);
                // fully-extracted families simply have an empty Server path.
                var roots = new List<string> { ArchitectureTestHelpers.CombinePath(protocolsPath, family) };
                if (_extractedFamilyRoots.TryGetValue(family, out var extractedRoots))
                {
                    roots.AddRange(extractedRoots.Select(r => ArchitectureTestHelpers.CombinePath(repositoryRoot, r)));
                }

                return roots
                    .SelectMany(EnumerateFamilySource)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
            },
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
                        $"family '{otherFamily}' ('{FamilyNamespace(otherFamily)}'). " +
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
