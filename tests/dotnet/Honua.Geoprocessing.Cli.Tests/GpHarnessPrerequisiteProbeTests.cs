// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Doctor's harness prerequisites (#6218). The probe takes a path lookup so the
/// suite does not need Docker, kind, kubectl, or Helm. Missing tools are reported;
/// they are not a doctor failure by themselves.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class GpHarnessPrerequisiteProbeTests
{
    private static readonly string[] Tools = ["docker", "kind", "kubectl", "helm"];

    [Fact]
    public void Probe_MissingTools_ReportsMissingStatusAndRemediation()
    {
        var reports = GpHarnessPrerequisiteProbe.Probe(static _ => null);

        reports.Select(static tool => tool.Name).Should().Equal(Tools);
        reports.Should().OnlyContain(tool => tool.Status == GpHarnessPrerequisiteProbe.Missing);
        reports.Should().OnlyContain(tool => !tool.Remediation.Contains('\n') && tool.Remediation.Contains("does not fail doctor"));
        reports.Single(static tool => tool.Name == "kind").Remediation.Should().Contain("gp-kind-lifecycle-matrix.py");
    }

    [Fact]
    public void Probe_PresentTools_ReportsFoundStatus()
    {
        var reports = GpHarnessPrerequisiteProbe.Probe(static name => "/usr/bin/" + name);

        reports.Should().OnlyContain(tool => tool.Status == GpHarnessPrerequisiteProbe.Found);
        reports.Should().OnlyContain(tool => tool.Remediation.Length > 0);
    }

    [Fact]
    public void Probe_OneMissingTool_ReportsOnlyThatToolMissing()
    {
        var reports = GpHarnessPrerequisiteProbe.Probe(static name => name == "helm" ? null : "/usr/bin/" + name);

        reports.Single(static tool => tool.Name == "helm").Status.Should().Be(GpHarnessPrerequisiteProbe.Missing);
        reports.Where(static tool => tool.Name != "helm").Should().OnlyContain(tool => tool.Status == GpHarnessPrerequisiteProbe.Found);
    }

    [Fact]
    public void FormatReport_MissingTools_PrintsStatusAndOneRemediationLineEach()
    {
        var text = GpHarnessPrerequisiteProbe.FormatReport(static _ => "  ");
        var lines = text.Split('\n');

        lines.Should().HaveCount(8);
        lines.Where((_, index) => index % 2 == 0).Should().Equal(Tools.Select(name => $"{name}: {GpHarnessPrerequisiteProbe.Missing}"));
        lines.Where((_, index) => index % 2 == 1).Should().OnlyContain(line => line.Contains("does not fail doctor"));
    }

    [Fact]
    public void WriteJson_FieldNames_MatchDoctorReport()
    {
        var report = new JsonObject();
        GpHarnessPrerequisiteProbe.WriteJson(report, static name => name is "docker" or "kubectl" ? "/bin/" + name : null);

        report.Select(static property => property.Key).Should().Equal(
            "docker",
            "dockerRemediation",
            "kind",
            "kindRemediation",
            "kubectl",
            "kubectlRemediation",
            "helm",
            "helmRemediation");
        report["docker"]!.GetValue<string>().Should().Be("found");
        report["kind"]!.GetValue<string>().Should().Be("missing");
        report["kubectl"]!.GetValue<string>().Should().Be("found");
        report["helm"]!.GetValue<string>().Should().Be("missing");
        report["kindRemediation"]!.GetValue<string>().Should().Contain("does not fail doctor");
    }

    [Fact]
    public void LookupOnPath_WindowsExtensions_ResolvesExeBeforeCmdAndBat()
    {
        var root = Directory.CreateTempSubdirectory("honua-gp-probe-");
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "docker"), "not-used");
            File.WriteAllText(Path.Join(root.FullName, "docker.cmd"), "later");
            File.WriteAllText(Path.Join(root.FullName, "docker.exe"), "first");

            GpHarnessPrerequisiteProbe.LookupOnPath("docker", root.FullName, windowsExecutableExtensions: true)
                .Should().Be(Path.Join(root.FullName, "docker.exe"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LookupOnPath_WindowsExtensions_IgnoresExtensionlessFile()
    {
        var root = Directory.CreateTempSubdirectory("honua-gp-probe-");
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "helm"), "unix-only");

            GpHarnessPrerequisiteProbe.LookupOnPath("helm", root.FullName, windowsExecutableExtensions: true)
                .Should().BeNull();
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LookupOnPath_UnixName_ResolvesExtensionlessFile()
    {
        var root = Directory.CreateTempSubdirectory("honua-gp-probe-");
        try
        {
            File.WriteAllText(Path.Join(root.FullName, "kind"), "kind");

            GpHarnessPrerequisiteProbe.LookupOnPath("kind", root.FullName, windowsExecutableExtensions: false)
                .Should().Be(Path.Join(root.FullName, "kind"));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void LookupOnPath_RootedOrSlashedName_ReturnsNull()
    {
        GpHarnessPrerequisiteProbe.LookupOnPath("/usr/bin/docker", "/usr/bin", windowsExecutableExtensions: false)
            .Should().BeNull();
        GpHarnessPrerequisiteProbe.LookupOnPath("docker/cli", "/usr/bin", windowsExecutableExtensions: false)
            .Should().BeNull();
    }
}
