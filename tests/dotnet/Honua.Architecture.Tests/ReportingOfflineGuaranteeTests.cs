// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces the "HTML reports render without external CDN dependencies" AC.
/// Scans every committed HTML golden for forbidden external patterns so a
/// later template change that introduces a CDN link fails the build rather
/// than silently breaking on-prem operators.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class ReportingOfflineGuaranteeTests
{
    private static readonly string[] _forbiddenPatterns =
    [
        "http://",
        "https://",
        "<script src=",
        "<link rel=\"stylesheet\" href=",
        "<link href=",
        "<iframe "
    ];

    [ArchitectureTest]
    public void HtmlReportGoldens_ContainNoExternalReferences()
    {
        var goldenDir = ResolveGoldensDirectory();
        Directory.Exists(goldenDir).Should().BeTrue(
            $"Reporting HTML goldens must live under {goldenDir}.");

        var goldens = Directory.GetFiles(goldenDir, "*.html");
        goldens.Should().NotBeEmpty("at least one HTML golden must be committed for the Reporting feature.");

        foreach (var path in goldens)
        {
            var body = File.ReadAllText(path);
            foreach (var pattern in _forbiddenPatterns)
            {
                body.Should().NotContain(pattern,
                    $"HTML golden '{Path.GetFileName(path)}' must be offline-safe " +
                    $"(no '{pattern}'). Reports are consumed by on-prem operators " +
                    "and must render without network access.");
            }
        }
    }

    [ArchitectureTest]
    public void EmbeddedReportCss_ShipsAsEmbeddedResource()
    {
        var cssPath = Path.Combine(
            ResolveRepoRoot(),
            "src",
            "Honua.Core",
            "Features",
            "Reporting",
            "Templates",
            "Resources",
            "report.css");
        File.Exists(cssPath).Should().BeTrue(
            $"Embedded report CSS must live at {cssPath}; the HTML renderer reads it via GetManifestResourceStream.");

        var csproj = File.ReadAllText(
            Path.Combine(ResolveRepoRoot(), "src", "Honua.Core", "Honua.Core.csproj"));
        csproj.Should().Contain("Features/Reporting/Templates/Resources/report.css",
            "report.css must be registered as <EmbeddedResource /> so the HTML renderer can inline it.");
    }

    private static string ResolveGoldensDirectory()
    {
        var repoRoot = ResolveRepoRoot();
        return Path.Combine(
            repoRoot,
            "tests",
            "dotnet",
            "Honua.Server.Tests",
            "Features",
            "Reporting",
            "Goldens");
    }

    private static string ResolveRepoRoot()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Honua.sln")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        throw new InvalidOperationException("Could not locate Honua.sln from the current test directory.");
    }
}
