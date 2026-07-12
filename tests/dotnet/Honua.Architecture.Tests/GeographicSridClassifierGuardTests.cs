// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.Core.Features.Shared.Models;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Ratchet for #2732: keeps geographic-SRID and Web Mercator alias classification unified behind
/// the canonical <see cref="GeographicSridClassifier"/> and
/// <see cref="SpatialReferenceExtensions.IsWebMercatorSrid(int)"/> /
/// <see cref="SpatialReferenceExtensions.NormalizeWebMercatorSrid(int)"/> helpers, and prevents new
/// local SRID allowlists from re-fragmenting the classification (the exact drift the audit found:
/// four independent "is this SRID geographic?" lists and three duplicated Web Mercator alias tables
/// that silently disagreed).
/// </summary>
/// <remarks>
/// The scan is a source-text ratchet in the spirit of the other arch tests
/// (<c>ThrowingDefaultInterfaceMethodRatchetTests</c>): it reads the shipped <c>src/**/*.cs</c> and
/// flags the two anti-patterns — an <c>is X or Y</c> chain of geographic EPSG codes, and a Web
/// Mercator alias table (the tell-tale <c>900913</c> alias juxtaposed with <c>or</c>/<c>,</c>/
/// <c>||</c>). New code must call <see cref="GeographicSridClassifier.IsGeographicSrid(int)"/> (or
/// the geodesic-safe variant) and <see cref="SpatialReferenceExtensions.IsWebMercatorSrid(int)"/>
/// instead of open-coding a literal list. The canonical files and a small set of deliberately-narrow
/// / protocol-specific heuristics are allowlisted below, each justified.
/// </remarks>
[Trait("Category", "Architecture")]
public sealed class GeographicSridClassifierGuardTests
{
    /// <summary>
    /// Source files permitted to contain SRID-classification literals. Every entry is either the
    /// canonical classifier itself or a documented, behaviour-locked heuristic that intentionally
    /// diverges from the canonical list (see #2732). Adding to this list requires a justification
    /// comment at the call site.
    /// </summary>
    private static readonly string[] AllowlistedRelativePaths =
    {
        // Canonical classifiers — the single sources of truth.
        "src/Honua.Core.Abstractions/Features/Shared/Models/GeographicSridClassifier.cs",
        "src/Honua.Core/Features/Shared/Models/SpatialReferenceExtensions.cs",

        // Deliberately-narrow / protocol-specific heuristics (justified at the call site):
        // CityGML srsName detection must also accept the deprecated 3D code EPSG:4327, which the
        // canonical axis-order list intentionally omits.
        "src/Honua.Scene/Publishing/CityGmlScenePublishExecutor.cs",
        // Import reprojection-suggestion heuristic intentionally keeps only the three highest-
        // confidence geographic codes as-is; widening would change import behaviour (out of scope).
        "src/Honua.Core/Features/FileImport/Services/ImportSchemaSuggestionService.cs",
    };

    /// <summary>
    /// An <c>is X or Y</c> chain of two geographic EPSG codes drawn from the canonical list. The
    /// alternation is built from <see cref="GeographicSridClassifier.GeographicSrids"/> so the guard
    /// tracks the canonical list automatically.
    /// </summary>
    private static readonly Regex GeographicChainRegex = BuildGeographicChainRegex();

    /// <summary>
    /// A Web Mercator alias table: the alias code <c>900913</c> juxtaposed with an <c>or</c>,
    /// comma, or <c>||</c> operator (the historical <c>is 3857 or 900913 or ...</c> /
    /// <c>{ 3857, 900913, ... }</c> shapes). Error-message strings use <c>/</c> separators and are
    /// not matched.
    /// </summary>
    private static readonly Regex WebMercatorAliasTableRegex =
        new(@"900913\s*(?:\bor\b|,|\|\|)|(?:\bor\b|,|\|\|)\s*900913", RegexOptions.Compiled);

    [ArchitectureTest]
    public void SourceTree_ShouldNotReintroduceLocalSridAllowlists()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var srcRoot = Path.Combine(repositoryRoot, "src");
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

            var mercatorMatch = WebMercatorAliasTableRegex.Match(source);
            if (mercatorMatch.Success)
            {
                violations.Add(
                    $"{normalized}: local Web Mercator alias table '{Collapse(mercatorMatch.Value)}'. " +
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
        var codes = string.Join('|', GeographicSridClassifier.GeographicSrids.Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        // Two geographic codes joined by an `or` pattern (C# switch/relational-pattern list), e.g.
        // `srid is 4326 or 4269`. One matching pair is enough to flag a chain.
        return new Regex($@"\b(?:{codes})\s+or\s+(?:{codes})\b", RegexOptions.Compiled);
    }

    private static string NormalizeSeparators(string path)
        => path.Replace('\\', '/');

    private static string Collapse(string value)
        => Regex.Replace(value, @"\s+", " ").Trim();
}
