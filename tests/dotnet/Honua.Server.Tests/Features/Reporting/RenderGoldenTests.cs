// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Reporting.Domain;
using Honua.Core.Features.Reporting.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.Reporting;

/// <summary>
/// Golden-file renderer tests. Each process family listed in the AC renders
/// to a committed Markdown/HTML body; byte-equal comparison ensures template
/// changes surface as reviewable diffs.
/// </summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class RenderGoldenTests
{
    /// <summary>
    /// Set this environment variable to <c>1</c> to rewrite goldens. Intended
    /// for intentional template updates; CI must run with the variable unset.
    /// </summary>
    private const string UpdateEnvVar = "HONUA_REPORTING_UPDATE_GOLDENS";

    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "analytics-buffer-aggregate", (Func<AnalysisResultPackage>)ReportingFixtures.BufferAggregatePackage };
        yield return new object[] { "analytics-density", (Func<AnalysisResultPackage>)ReportingFixtures.DensityPackage };
        yield return new object[] { "surface-slope", (Func<AnalysisResultPackage>)ReportingFixtures.SlopePackage };
        yield return new object[] { "generalization-dissolve", (Func<AnalysisResultPackage>)ReportingFixtures.DissolvePackage };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Category", "Integration")]
    [Operation(Operations.Metadata)]
    public async Task MarkdownGoldenMatches(string goldenName, Func<AnalysisResultPackage> factory)
    {
        var renderer = new MarkdownReportRenderer();
        var report = await BuildReportAsync(factory());
        var actual = Normalize(renderer.Render(report));

        AssertGoldenMatches(goldenName + ".md", actual);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("Category", "Integration")]
    [Operation(Operations.Metadata)]
    public async Task HtmlGoldenMatches(string goldenName, Func<AnalysisResultPackage> factory)
    {
        var renderer = new HtmlReportRenderer();
        var report = await BuildReportAsync(factory());
        var actual = Normalize(renderer.Render(report));

        AssertGoldenMatches(goldenName + ".html", actual);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    public void HtmlGoldens_ContainNoExternalReferences()
    {
        foreach (var golden in Directory.EnumerateFiles(GoldenDirectory(), "*.html"))
        {
            var body = File.ReadAllText(golden);
            body.Should().NotContain("http://", $"{Path.GetFileName(golden)} must be offline-safe");
            body.Should().NotContain("https://", $"{Path.GetFileName(golden)} must be offline-safe");
            body.Should().NotContain("<script src=", $"{Path.GetFileName(golden)} must not load client scripts");
            body.Should().NotContain("<link rel=\"stylesheet\" href=",
                $"{Path.GetFileName(golden)} must not load external stylesheets");
        }
    }

    private static async Task<AnalysisReport> BuildReportAsync(AnalysisResultPackage package)
    {
        var builder = ReportingFixtures.CreateBuilder();
        return await builder.BuildAsync("job-golden", package, CancellationToken.None);
    }

    private static void AssertGoldenMatches(string relativeName, string actual)
    {
        var goldenPath = Path.Combine(GoldenDirectory(), relativeName);
        if (Environment.GetEnvironmentVariable(UpdateEnvVar) == "1")
        {
            Directory.CreateDirectory(GoldenDirectory());
            File.WriteAllText(goldenPath, actual);
        }

        File.Exists(goldenPath).Should().BeTrue(
            $"golden '{goldenPath}' must be committed. Run the test suite with {UpdateEnvVar}=1 to regenerate.");
        var expected = Normalize(File.ReadAllText(goldenPath));
        actual.Should().Be(expected, $"golden '{relativeName}' must match rendered output");
    }

    private static string GoldenDirectory()
    {
        var assemblyLocation = typeof(RenderGoldenTests).GetTypeInfo().Assembly.Location;
        var binDir = Path.GetDirectoryName(assemblyLocation)!;
        // bin/Debug/net10.0 -> repo-relative Features/Reporting/Goldens
        var projectDir = Path.GetFullPath(Path.Combine(binDir, "..", "..", ".."));
        return Path.Combine(projectDir, "Features", "Reporting", "Goldens");
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
