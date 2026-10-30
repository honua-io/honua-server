// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Nodes;
using FluentAssertions;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// Project discovery (#6217): a scaffolded custom task reports its own prerequisites.
/// A catalog registration is not a substitute, and a missing dependency is never a remote submit.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class GpProjectDiscoveryTests
{
    private static GpProjectManifest Manifest(GpProjectLanguage language, string? toolchain, string? dependencyLock = null) => new()
    {
        Name = "parcel-summary",
        Language = language,
        Entrypoint = language == GpProjectLanguage.Python ? "summary_tool:execute" : "SummaryTool::Honua.Templates.SummaryTool",
        Source = "process",
        FixtureParams = "fixtures/params.json",
        FixtureInputs = new Dictionary<string, string>(StringComparer.Ordinal) { ["features"] = "fixtures/input.geojson" },
        Expected = "expected",
        ToolchainVersion = toolchain,
        DependencyLock = dependencyLock,
    };

    private static GpEnvironmentProbe Probe(bool python, bool dotnet) => new(
        hostGdalAvailable: false,
        workerImageAvailable: null,
        pythonAvailable: python,
        integrationStackConfigured: false,
        executableAvailable: name => dotnet && name == "dotnet");

    [Fact]
    public void Python_WithRuntimeLockAndMatchingDevkit_IsRunnableHere()
    {
        using var directory = new TempProject();
        directory.Write("requirements.lock", "# locked\n");
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: true, dotnet: false),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);

        verdict.ExecutionMode.Should().Be(GpExecutionMode.Custom);
        verdict.RunnableHere.Should().BeTrue();
        verdict.BlockedBy.Should().BeNull();
        verdict.SupportedTargets.Should().Equal("local-harness", "aws-batch");
        verdict.Prerequisites.Select(static p => p.Name).Should().Equal(
            GpProjectDiscovery.PythonRuntimeName,
            "dependency lock (requirements.lock)",
            GpProjectDiscovery.HarnessName);
        verdict.Prerequisites.Should().OnlyContain(static p => p.Status == "satisfied");
        verdict.Prerequisites[2].Detail.Should().Contain("restores .honua/toolchain").And.Contain(GpProjectDiscovery.NoSubmit);
    }

    [Fact]
    public void Python_MissingInterpreter_IsMissingNotUnchecked_AndDoesNotSubmit()
    {
        using var directory = new TempProject();
        directory.Write("requirements.lock", "# locked\n");
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: false, dotnet: true),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);

        verdict.RunnableHere.Should().BeFalse();
        verdict.BlockedBy.Should().Be(GpProjectDiscovery.PythonRuntimeName);
        verdict.Prerequisites[0].Status.Should().Be("missing");
        verdict.Prerequisites[0].Detail.Should().Contain("Install Python 3.11").And.Contain(GpProjectDiscovery.NoSubmit);
    }

    [Fact]
    public void Dotnet_DoesNotTreatPythonAsItsRuntime()
    {
        using var directory = new TempProject();
        directory.Write("process/packages.lock.json", "{}\n");
        var runnable = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Dotnet, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: false, dotnet: true),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);
        runnable.RunnableHere.Should().BeTrue();
        runnable.Prerequisites[0].Name.Should().Be(GpProjectDiscovery.DotnetRuntimeName);

        var blocked = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Dotnet, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: true, dotnet: false),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);
        blocked.RunnableHere.Should().BeFalse();
        blocked.BlockedBy.Should().Be(GpProjectDiscovery.DotnetRuntimeName);
        blocked.Prerequisites[0].Detail.Should().Contain(".NET 10 SDK").And.Contain(GpProjectDiscovery.NoSubmit);
    }

    [Fact]
    public void MissingLock_BlocksEvenWhenTheRuntimeAndHarnessMatch()
    {
        using var directory = new TempProject();
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: true, dotnet: true),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);

        verdict.RunnableHere.Should().BeFalse();
        verdict.BlockedBy.Should().Be("dependency lock (requirements.lock)");
        verdict.Prerequisites[1].Status.Should().Be("missing");
        verdict.Prerequisites[1].Detail.Should().Contain(GpProjectDiscovery.NoSubmit);
        GpProjectDiscovery.BlockedError(Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version), verdict)
            .Should().Contain("parcel-summary").And.Contain("dependency lock (requirements.lock)").And.Contain(GpProjectDiscovery.NoSubmit);
    }

    [Fact]
    public void LockPath_OutsideTheProject_IsMissing()
    {
        using var directory = new TempProject();
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version, dependencyLock: "../requirements.lock"),
            directory.Path,
            Probe(python: true, dotnet: true),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);

        verdict.BlockedBy.Should().Be("dependency lock (../requirements.lock)");
        verdict.Prerequisites[1].Detail.Should().Contain("relative path");
    }

    [Fact]
    public void MismatchedToolchain_RefusesInsteadOfRestoring()
    {
        using var directory = new TempProject();
        directory.Write("requirements.lock", "# locked\n");
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, "other-version"),
            directory.Path,
            Probe(python: true, dotnet: true),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);

        verdict.RunnableHere.Should().BeFalse();
        verdict.BlockedBy.Should().Be(GpProjectDiscovery.HarnessName);
        verdict.Prerequisites[2].Detail.Should().Contain("other-version").And.Contain(GpBundledToolchain.Version).And.Contain("--harness-root");
    }

    [Fact]
    public void ExplicitHarnessRoot_SatisfiesTheHarnessWithoutAVersionMatch()
    {
        using var directory = new TempProject();
        directory.Write("requirements.lock", "# locked\n");
        var harness = directory.WriteHarness();
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, "other-version"),
            directory.Path,
            Probe(python: true, dotnet: false),
            resolvedHarnessRoot: harness,
            allowInstalledRestore: false);

        verdict.RunnableHere.Should().BeTrue();
        verdict.Prerequisites[2].Detail.Should().Contain(harness);
    }

    [Fact]
    public void WriteTo_KeepsTheCatalogFieldSet()
    {
        using var directory = new TempProject();
        directory.Write("requirements.lock", "# locked\n");
        var verdict = GpProjectDiscovery.Assess(
            Manifest(GpProjectLanguage.Python, GpBundledToolchain.Version),
            directory.Path,
            Probe(python: true, dotnet: false),
            resolvedHarnessRoot: null,
            allowInstalledRestore: true);
        var descriptor = new JsonObject();

        verdict.WriteTo(descriptor);

        descriptor.Select(static p => p.Key).Should().Equal("executionMode", "prerequisites", "runnableHere", "blockedBy", "supportedTargets");
        descriptor["executionMode"]!.GetValue<string>().Should().Be("custom");
        descriptor["runnableHere"]!.GetValue<bool>().Should().BeTrue();
    }

    private sealed class TempProject : IDisposable
    {
        public TempProject() => Path = Directory.CreateTempSubdirectory("gp-discover-").FullName;

        public string Path { get; }

        public void Write(string relative, string contents)
        {
            var full = System.IO.Path.Join(Path, relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
            File.WriteAllText(full, contents);
        }

        public string WriteHarness()
        {
            var harness = System.IO.Path.Join(Path, "harness");
            Directory.CreateDirectory(System.IO.Path.Join(harness, "docker", "worker-customcode-python", "harness"));
            Directory.CreateDirectory(System.IO.Path.Join(harness, "docker", "worker-customcode-dotnet", "harness"));
            return harness;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // The test already recorded its assertion; a leftover temp dir is not the result.
            }
        }
    }
}
