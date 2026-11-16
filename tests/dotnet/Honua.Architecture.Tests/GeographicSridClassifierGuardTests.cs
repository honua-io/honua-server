// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Architecture.Tests;

[Trait("Category", "Architecture")]
public sealed class GeographicSridClassifierGuardTests
{
    private static readonly string[] AllowlistedRelativePaths =
    {
        // Canonical classifiers — the single sources of truth.
        "src/Honua.Core/Abstractions/Features/Shared/Models/GeographicSridClassifier.cs",
        "src/Honua.Core/Core/Features/Shared/Models/SpatialReferenceExtensions.cs",

        // Deliberately-narrow / protocol-specific heuristics (justified at the call site):
        // CityGML srsName detection must also accept the deprecated 3D code EPSG:4327, which the
        // canonical axis-order list intentionally omits.
        "src/Honua.Scene/Publishing/CityGmlScenePublishExecutor.cs",
        // Import reprojection-suggestion heuristic intentionally keeps only the three highest-
        // confidence geographic codes as-is; widening would change import behaviour (out of scope).
        "src/Honua.Core/Core/Features/FileImport/Services/ImportSchemaSuggestionService.cs",
    };

    private static readonly int[] WebMercatorAliasCodes = { 3857, 900913, 102100, 102113, 3785 };

    private static readonly Regex GeographicChainRegex = BuildGeographicChainRegex();

    private static readonly Regex GeographicEqualityChainRegex = BuildGeographicEqualityChainRegex();

    private static readonly Regex WebMercatorAliasTableRegex =
        new(@"900913\s*(?:\bor\b|,|\|\|)|(?:\bor\b|,|\|\|)\s*900913", RegexOptions.Compiled);

    private static readonly Regex WebMercatorMultiAliasRegex = BuildWebMercatorMultiAliasRegex();

    [ArchitectureTest]
    public void SourceTree_ShouldNotReintroduceLocalSridAllowlists()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var srcRoot = ArchitectureTestHelpers.CombinePath(repositoryRoot, "src");
        Directory.Exists(srcRoot).Should().BeTrue("the src tree must exist to scan for SRID allowlists");

        var allowlist = new HashSet<string>(
            AllowlistedRelativePaths.Select(NormalizeSeparators),
            StringComparer.OrdinalIgnoreCase);

        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = NormalizeSeparators(Path.GetRelativePath(repositoryRoot, file));
            if (normalized.Contains("/obj/", StringComparison.Ordinal) ||
                normalized.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            if (allowlist.Contains(normalized))
            {
                continue;
            }

            var source = File.ReadAllText(file);

            var geographicMatch = GeographicChainRegex.Match(source);
            if (geographicMatch.Success)
            {
                violations.Add(
                    $"{normalized}: local geographic-SRID allowlist '{Collapse(geographicMatch.Value)}'. " +
                    "Call GeographicSridClassifier.IsGeographicSrid (or IsGeodesicDistanceSafeSrid) instead.");
            }

            var geographicEqualityMatch = GeographicEqualityChainRegex.Match(source);
            if (geographicEqualityMatch.Success)
            {
                violations.Add(
                    $"{normalized}: local geographic-SRID equality chain '{Collapse(geographicEqualityMatch.Value)}'. " +
                    "Call GeographicSridClassifier.IsGeographicSrid (or IsGeodesicDistanceSafeSrid) instead.");
            }

            var mercatorMatch = WebMercatorAliasTableRegex.Match(source);
            if (mercatorMatch.Success)
            {
                violations.Add(
                    $"{normalized}: local Web Mercator alias table '{Collapse(mercatorMatch.Value)}'. " +
                    "Call SpatialReferenceExtensions.IsWebMercatorSrid / NormalizeWebMercatorSrid instead.");
            }

            var mercatorMultiMatch = WebMercatorMultiAliasRegex.Match(source);
            if (mercatorMultiMatch.Success)
            {
                violations.Add(
                    $"{normalized}: local Web Mercator alias table '{Collapse(mercatorMultiMatch.Value)}'. " +
                    "Call SpatialReferenceExtensions.IsWebMercatorSrid / NormalizeWebMercatorSrid instead.");
            }
        }

        violations.Should().BeEmpty(
            "geographic-SRID and Web Mercator classification must stay unified behind the canonical " +
            "helpers (#2732). If a divergence is genuinely required, route through the canonical " +
            "classifier or add the file to the justified allowlist with a call-site comment. " +
            "Violations:\n{0}",
            string.Join("\n", violations));
    }

    private static Regex BuildGeographicChainRegex()
    {
        var codes = GeographicCodeAlternation();
        // Two geographic codes joined by an `or` pattern (C# switch/relational-pattern list), e.g.
        // `srid is 4326 or 4269`. One matching pair is enough to flag a chain.
        return new Regex($@"\b(?:{codes})\s+or\s+(?:{codes})\b", RegexOptions.Compiled);
    }

    private static Regex BuildGeographicEqualityChainRegex()
    {
        var codes = GeographicCodeAlternation();
        // Two geographic codes each compared with `==` and joined by `||`/`or`, e.g.
        // `srid == 4326 || srid == 4269`. The bounded gaps keep the match on a single expression and
        // avoid pairing a code with an unrelated `==` elsewhere in the file.
        return new Regex(
            $@"==\s*(?:{codes})\b[^;\n]{{0,60}}?(?:\|\||\bor\b)[^;\n]{{0,60}}?==\s*(?:{codes})\b",
            RegexOptions.Compiled);
    }

    private static Regex BuildWebMercatorMultiAliasRegex()
    {
        var codes = string.Join('|', WebMercatorAliasCodes.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Two Web Mercator codes joined by an `or`/`||` boolean alternation. Comma is intentionally
        // excluded so `wkid = 102100, latestWkid = 3857` metadata initializers do not match.
        return new Regex(
            $@"\b(?:{codes})\b\s*(?:\bor\b|\|\|)\s*[^;\n]{{0,30}}?\b(?:{codes})\b",
            RegexOptions.Compiled);
    }

    private static string GeographicCodeAlternation()
        => string.Join('|', GeographicSridClassifier.GeographicSrids.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    private static string NormalizeSeparators(string path)
        => path.Replace('\\', '/');

    private static string Collapse(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();
}
