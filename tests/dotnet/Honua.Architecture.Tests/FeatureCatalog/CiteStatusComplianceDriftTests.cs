// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Drift guard for the CITE canonical-numbers join (#2924).
/// <c>docs/cite-status.md</c> is the single canonical snapshot of OGC CITE
/// per-suite pass rates. The <c>x-honua-cite-compliance</c> vendor extension
/// embedded in the <c>info</c> object of <c>src/Honua.Server/openapi.json</c>
/// and the four protocol-scoped <c>*-openapi.json</c> files
/// (<c>ogc-processes</c>, <c>ogc-tiles</c>, <c>ogc-coverages</c>,
/// <c>ogc-maps</c>) each restate a subset of the same numbers. Before this
/// guard, nothing checked that join — a hand-edit to any one of the five
/// artifacts could silently diverge from cite-status.md forever.
/// </summary>
[Trait("Category", "Architecture")]
public sealed partial class CiteStatusComplianceDriftTests
{
    private const string CiteStatusRelativePath = "docs/cite-status.md";

    private static readonly (string DisplayPath, string[] Segments)[] VendorExtensionArtifacts =
    [
        ("src/Honua.Server/openapi.json", ["src", "Honua.Server", "openapi.json"]),
        ("src/Honua.Server/ogc-processes-openapi.json", ["src", "Honua.Server", "ogc-processes-openapi.json"]),
        ("src/Honua.Server/ogc-tiles-openapi.json", ["src", "Honua.Server", "ogc-tiles-openapi.json"]),
        ("src/Honua.Server/ogc-coverages-openapi.json", ["src", "Honua.Server", "ogc-coverages-openapi.json"]),
        ("src/Honua.Server/ogc-maps-openapi.json", ["src", "Honua.Server", "ogc-maps-openapi.json"]),
    ];

    // Mirrors scripts/ci/generate-capability-matrix.py's parse_cite_status row
    // pattern exactly (same capture groups, same anchors) so the Python
    // capability-matrix generator and this architecture-test gate can never
    // silently disagree about what counts as a per-suite row in
    // docs/cite-status.md's "Current Per-Protocol Status" table.
    [GeneratedRegex(
        @"^\|\s*([^|]+?)\s*\|\s*`?([^|`]+?)`?\s*\|\s*(\d+)\s*/\s*(\d+)\s*\|\s*([\d.]+)%\s*\|",
        RegexOptions.Multiline)]
    private static partial Regex CiteStatusRowPattern();

    // Matches the aggregate-restatement phrasing used across the vendor
    // extensions' "summary" prose: either "<passed>/<total> across <n>
    // conformance suites" (ogc-processes-openapi.json, which has no official
    // CITE ETS of its own and only restates the full-suite aggregate) or
    // "full Honua suite is <passed>/<total>" (the protocol-scoped
    // ogc-tiles/ogc-coverages/ogc-maps files, which also carry a suites[]
    // array for their own suite(s) but redundantly mention the full-suite
    // aggregate too). The trailing "across <n> conformance suites" clause is
    // optional so both phrasings share one pattern.
    [GeneratedRegex(
        @"(?:full Honua suite is|surrounding Honua OGC stack passes)\s+(\d+)\s*/\s*(\d+)(?:\s+across\s+(\d+)\s+conformance suites)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex ProseAggregatePattern();

    // Catches every "<passed>/<total>" fraction in the summary prose, not just the
    // full-suite aggregate ProseAggregatePattern anchors on. The protocol-scoped
    // *-openapi.json files restate their own suite's count ahead of the aggregate
    // (e.g. "OGC API Tiles ETS passes 16/16 on trunk; full Honua suite is
    // 1117/1117") -- AssertSuiteRows only checks the structured suites[] entries,
    // so without this a stale leading count in the prose would slip past the gate
    // even though suites[] and cite-status.md still agree.
    [GeneratedRegex(@"\b(\d+)\s*/\s*(\d+)\b")]
    private static partial Regex ProseFractionPattern();

    [ArchitectureTest]
    public void EveryVendorExtension_AgreesWithCiteStatusPerSuiteTotals()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var canonical = ParseCanonicalSuites(repoRoot);

        canonical.Should().NotBeEmpty(
            $"{CiteStatusRelativePath}'s 'Current Per-Protocol Status' table must have at least one parseable " +
            "suite row (| Suite | Profile | Passed / Total | Pass Rate | ... |) or this drift gate has nothing " +
            "to check the OpenAPI vendor extensions against.");

        foreach (var (suite, row) in canonical)
        {
            row.Passed.Should().Be(row.Total,
                $"{CiteStatusRelativePath}'s row for suite '{suite}' reports {row.Passed}/{row.Total} passed/total. " +
                "The page's own 'Public Evidence Standard' only permits listing suites at 100% pass -- fix or " +
                $"remove the row in {CiteStatusRelativePath} before this gate can trust it as canonical.");
        }

        var canonicalTotalPassed = canonical.Values.Sum(row => row.Passed);

        foreach (var (displayPath, segments) in VendorExtensionArtifacts)
        {
            var absolutePath = ArchitectureTestHelpers.CombinePath([repoRoot, .. segments]);
            using var document = JsonDocument.Parse(File.ReadAllBytes(absolutePath));
            var extension = document.RootElement.GetProperty("info").GetProperty("x-honua-cite-compliance");

            AssertAuthoritativeSource(displayPath, extension);

            var hasTotals = extension.TryGetProperty("totals", out var totalsElement);
            var hasSuites = extension.TryGetProperty("suites", out var suitesElement);

            if (hasTotals)
            {
                AssertTotals(displayPath, totalsElement, canonicalTotalPassed);
            }

            // Required only when the extension has neither a totals object nor a
            // suites array to check (ogc-processes-openapi.json today) -- that
            // leaves prose as the only place its aggregate claim could possibly
            // be gated. Where a totals object or suites array already exists,
            // any prose aggregate mention found is still validated, but its
            // absence is not itself a failure.
            AssertProseAggregate(displayPath, extension, canonicalTotalPassed, canonical.Count, required: !hasTotals && !hasSuites);

            if (hasSuites)
            {
                AssertSuiteRows(displayPath, suitesElement, canonical);
            }

            AssertEveryProseFractionIsKnown(displayPath, extension, canonicalTotalPassed, hasSuites ? suitesElement : null);
        }
    }

    [ArchitectureTest]
    public void PrimaryOpenApiVendorExtension_ListsEveryCiteStatusSuite()
    {
        // src/Honua.Server/openapi.json is the only vendor extension whose
        // summary claims to cover the *full* suite set ("All 11 conformance
        // suites pass at 100% on trunk"), so it -- and only it -- must be a
        // complete mirror of cite-status.md's suite set, not merely a
        // non-diverging subset like the protocol-scoped *-openapi.json files.
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var canonical = ParseCanonicalSuites(repoRoot);

        var primaryPath = ArchitectureTestHelpers.CombinePath(repoRoot, "src", "Honua.Server", "openapi.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(primaryPath));
        var extension = document.RootElement.GetProperty("info").GetProperty("x-honua-cite-compliance");
        var suitesElement = extension.GetProperty("suites");

        var extensionSuiteNames = suitesElement.EnumerateArray()
            .Select(entry => entry.GetProperty("suite").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.Ordinal);

        var missing = canonical.Keys
            .Where(suite => !extensionSuiteNames.Contains(suite))
            .OrderBy(suite => suite, StringComparer.Ordinal)
            .ToArray();
        var extra = extensionSuiteNames
            .Where(suite => !canonical.ContainsKey(suite))
            .OrderBy(suite => suite, StringComparer.Ordinal)
            .ToArray();

        missing.Should().BeEmpty(
            $"src/Honua.Server/openapi.json's x-honua-cite-compliance.suites is missing [{string.Join(", ", missing)}], " +
            $"which {CiteStatusRelativePath} lists -- add the missing row(s) to openapi.json's suites array so the two " +
            "never silently disagree about which suites exist.");
        extra.Should().BeEmpty(
            $"src/Honua.Server/openapi.json's x-honua-cite-compliance.suites has stale suite(s) [{string.Join(", ", extra)}] " +
            $"that no longer exist in {CiteStatusRelativePath}'s table -- remove the stale row(s) from openapi.json, or add " +
            $"the suite to {CiteStatusRelativePath} if it is a genuinely new CITE suite.");
    }

    private static void AssertAuthoritativeSource(string displayPath, JsonElement extension)
    {
        var hasSource = extension.TryGetProperty("authoritativeSource", out var sourceElement);
        hasSource.Should().BeTrue(
            $"{displayPath}'s x-honua-cite-compliance extension must declare an authoritativeSource field pointing " +
            $"at {CiteStatusRelativePath} so readers know which page wins on divergence.");
        sourceElement.GetString().Should().Be(CiteStatusRelativePath,
            $"{displayPath}'s x-honua-cite-compliance.authoritativeSource must be '{CiteStatusRelativePath}' -- " +
            $"update the extension in {displayPath} (not {CiteStatusRelativePath}) to fix this.");
    }

    private static void AssertTotals(string displayPath, JsonElement totalsElement, int canonicalTotalPassed)
    {
        var passed = totalsElement.GetProperty("passed").GetInt32();
        var failed = totalsElement.GetProperty("failed").GetInt32();
        var skipped = totalsElement.GetProperty("skipped").GetInt32();
        var cantTell = totalsElement.GetProperty("cantTell").GetInt32();

        passed.Should().Be(canonicalTotalPassed,
            $"{displayPath}'s x-honua-cite-compliance.totals.passed is {passed}, but the sum of Passed across every " +
            $"row in {CiteStatusRelativePath} is {canonicalTotalPassed}. Regenerate {displayPath}'s totals from the " +
            $"latest CITE Evidence Report run, or correct {CiteStatusRelativePath} if the OpenAPI extension already " +
            "reflects a newer one.");
        failed.Should().Be(0,
            $"{displayPath}'s x-honua-cite-compliance.totals.failed is {failed}, but {CiteStatusRelativePath} only " +
            $"ever documents suites at 100% pass -- a nonzero failed count means {displayPath} is stale.");
        skipped.Should().Be(0,
            $"{displayPath}'s x-honua-cite-compliance.totals.skipped is {skipped}, but {CiteStatusRelativePath} only " +
            $"ever documents suites at 100% pass -- a nonzero skipped count means {displayPath} is stale.");
        cantTell.Should().Be(0,
            $"{displayPath}'s x-honua-cite-compliance.totals.cantTell is {cantTell}, but {CiteStatusRelativePath} only " +
            $"ever documents suites at 100% pass -- a nonzero cantTell count means {displayPath} is stale.");
    }

    private static void AssertProseAggregate(
        string displayPath,
        JsonElement extension,
        int canonicalTotalPassed,
        int canonicalSuiteCount,
        bool required)
    {
        var summary = extension.TryGetProperty("summary", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;
        var match = ProseAggregatePattern().Match(summary);

        if (!match.Success)
        {
            // Only a hard failure when this was the last remaining place this
            // extension could be gated at all (no totals object, no suites
            // array). Otherwise a missing/differently-worded aggregate mention
            // is fine -- the totals/suites checks already cover the claim.
            required.Should().BeFalse(
                $"{displayPath}'s x-honua-cite-compliance.summary (\"{summary}\") has no totals object, no suites " +
                "array, and no recognizable aggregate pass-count phrase either (\"<passed>/<total> across <n> " +
                "conformance suites\" or \"full Honua suite is <passed>/<total>\"), so this drift gate cannot check " +
                $"its claim at all -- add one of the three so it stays gated against {CiteStatusRelativePath}.");
            return;
        }

        var passed = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var total = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);

        passed.Should().Be(canonicalTotalPassed,
            $"{displayPath}'s x-honua-cite-compliance.summary claims {passed}/{total} passed, but " +
            $"{CiteStatusRelativePath}'s per-suite table sums to {canonicalTotalPassed} passed -- update the summary " +
            $"prose in {displayPath} to match {CiteStatusRelativePath}.");
        total.Should().Be(canonicalTotalPassed,
            $"{displayPath}'s x-honua-cite-compliance.summary claims {passed}/{total}, which is not a 100%-pass claim " +
            $"({canonicalTotalPassed}/{canonicalTotalPassed} expected); {CiteStatusRelativePath} only documents " +
            "100%-pass suites, so this prose must read a matching total.");

        if (match.Groups[3].Success)
        {
            var suiteCount = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            suiteCount.Should().Be(canonicalSuiteCount,
                $"{displayPath}'s x-honua-cite-compliance.summary claims {suiteCount} conformance suites, but " +
                $"{CiteStatusRelativePath}'s 'Current Per-Protocol Status' table has {canonicalSuiteCount} rows -- " +
                $"update the summary prose in {displayPath} to match {CiteStatusRelativePath}.");
        }
    }

    /// <summary>
    /// Validates every "&lt;passed&gt;/&lt;total&gt;" fraction mentioned anywhere in the
    /// extension's summary prose -- not just the full-suite aggregate
    /// <see cref="ProseAggregatePattern"/> anchors on. The protocol-scoped
    /// *-openapi.json files restate their own suite's count ahead of the aggregate
    /// (e.g. "OGC API Tiles ETS passes 16/16 on trunk; full Honua suite is
    /// 1117/1117"); <see cref="AssertSuiteRows"/> only checks the structured
    /// suites[] entries, so a stale leading count in the prose would otherwise
    /// slip past this gate even when suites[] and cite-status.md still agree.
    /// Each fraction found must equal either the canonical full-suite aggregate
    /// or one of this file's own suites[] (passed, total) pairs.
    /// </summary>
    private static void AssertEveryProseFractionIsKnown(
        string displayPath,
        JsonElement extension,
        int canonicalTotalPassed,
        JsonElement? suitesElement)
    {
        var summary = extension.TryGetProperty("summary", out var summaryElement)
            ? summaryElement.GetString() ?? string.Empty
            : string.Empty;

        (int Passed, int Total)[] knownSuiteTotals = suitesElement is null
            ? []
            : [.. suitesElement.Value.EnumerateArray()
                .Select(suite => (Passed: suite.GetProperty("passed").GetInt32(), Total: suite.GetProperty("total").GetInt32()))];

        foreach (Match fraction in ProseFractionPattern().Matches(summary))
        {
            var passed = int.Parse(fraction.Groups[1].Value, CultureInfo.InvariantCulture);
            var total = int.Parse(fraction.Groups[2].Value, CultureInfo.InvariantCulture);

            var isCanonicalAggregate = passed == canonicalTotalPassed && total == canonicalTotalPassed;
            var isKnownSuiteTotal = knownSuiteTotals.Any(suite => suite.Passed == passed && suite.Total == total);

            (isCanonicalAggregate || isKnownSuiteTotal).Should().BeTrue(
                $"{displayPath}'s x-honua-cite-compliance.summary mentions \"{passed}/{total}\", which matches " +
                $"neither the canonical full-suite aggregate ({canonicalTotalPassed}/{canonicalTotalPassed}) nor " +
                $"any per-suite total in {displayPath}'s own suites[] array -- the summary prose has drifted from " +
                $"{CiteStatusRelativePath} (or {displayPath}'s own suites[] entries). Update the summary in " +
                $"{displayPath} to match.");
        }
    }

    private static void AssertSuiteRows(
        string displayPath,
        JsonElement suitesElement,
        IReadOnlyDictionary<string, CanonicalSuiteRow> canonical)
    {
        foreach (var suiteElement in suitesElement.EnumerateArray())
        {
            var suiteName = suiteElement.GetProperty("suite").GetString() ?? string.Empty;

            var found = canonical.TryGetValue(suiteName, out var expected);
            found.Should().BeTrue(
                $"{displayPath}'s x-honua-cite-compliance.suites references suite '{suiteName}', which does not " +
                $"exist in {CiteStatusRelativePath}'s 'Current Per-Protocol Status' table -- rename or remove the " +
                $"stale entry in {displayPath}, or add the suite to {CiteStatusRelativePath} if it is genuinely new.");
            if (!found)
            {
                continue;
            }

            var profile = suiteElement.GetProperty("profile").GetString();
            var passed = suiteElement.GetProperty("passed").GetInt32();
            var total = suiteElement.GetProperty("total").GetInt32();
            var passRate = suiteElement.GetProperty("passRate").GetString();

            profile.Should().Be(expected!.Profile,
                $"{displayPath}'s suite '{suiteName}' declares profile '{profile}', but {CiteStatusRelativePath} " +
                $"declares '{expected.Profile}' -- fix whichever side is stale.");
            passed.Should().Be(expected.Passed,
                $"{displayPath}'s suite '{suiteName}' reports {passed}/{total} passed/total, but " +
                $"{CiteStatusRelativePath} reports {expected.Passed}/{expected.Total} -- update {displayPath} from " +
                "the latest evidence run.");
            total.Should().Be(expected.Total,
                $"{displayPath}'s suite '{suiteName}' reports {passed}/{total} passed/total, but " +
                $"{CiteStatusRelativePath} reports {expected.Passed}/{expected.Total} -- update {displayPath} from " +
                "the latest evidence run.");
            passRate.Should().Be(expected.PassRateText,
                $"{displayPath}'s suite '{suiteName}' reports passRate '{passRate}', but {CiteStatusRelativePath} " +
                $"reports '{expected.PassRateText}' -- update {displayPath} from the latest evidence run.");
        }
    }

    private static Dictionary<string, CanonicalSuiteRow> ParseCanonicalSuites(string repoRoot)
    {
        var path = ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "cite-status.md");
        var text = File.ReadAllText(path);

        var suites = new Dictionary<string, CanonicalSuiteRow>(StringComparer.Ordinal);
        foreach (Match match in CiteStatusRowPattern().Matches(text))
        {
            var suite = match.Groups[1].Value.Trim();
            if (suite is "Suite" or "---")
            {
                continue;
            }

            suites[suite] = new CanonicalSuiteRow(
                Profile: match.Groups[2].Value.Trim(),
                Passed: int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                Total: int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture),
                PassRateText: $"{match.Groups[5].Value}%");
        }

        return suites;
    }

    private sealed record CanonicalSuiteRow(string Profile, int Passed, int Total, string PassRateText);
}
