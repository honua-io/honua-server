// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Geoprocessing.Cli;
using Honua.Geoprocessing.Cli.Release;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

/// <summary>
/// `honua-gp-devkit init` / `test` building blocks (#6217): the embedded honua-samples
/// templates match their manifests (the drift anchor), the flat YAML descriptor round-trips,
/// the scaffold adds only the descriptor (and the .NET response file), and the artifact
/// comparison names the first difference.
/// </summary>
[Trait("Tier", "Fast")]
public sealed class GpCustomProjectTests
{
    [Fact]
    public void FailureDiagnostics_PreservesCompilerErrorsAlongsideWorkloadWarning()
    {
        var diagnostic = GpCustomProjectTester.FailureDiagnostics(
            "SummaryTool.cs(12,3): error CS1002: ; expected",
            "An issue was encountered verifying workloads.", 1600);

        diagnostic.Should().Contain("error CS1002").And.Contain("verifying workloads");
    }

    [Fact]
    public void FailureDiagnostics_BoundsBothStreamsWithoutDroppingEitherTail()
    {
        var diagnostic = GpCustomProjectTester.FailureDiagnostics(
            new string('a', 5000) + "compiler-tail", new string('b', 5000) + "warning-tail", 1600);

        diagnostic.Length.Should().BeLessThanOrEqualTo(1600);
        diagnostic.Should().Contain("compiler-tail").And.Contain("warning-tail");
        GpCustomProjectTester.FailureDiagnostics("only output", " ", 1600).Should().Be("only output");
    }

    public static bool PythonAvailable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var names = OperatingSystem.IsWindows() ? new[] { "python3.exe", "python.exe" } : ["python3", "python"];
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(dir => names.Any(n => File.Exists(Path.Join(dir, n))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Update_ReconcilesRemovedArtifactsAndGoldenDigest(bool removeGoldenArtifact)
    {
        var directory = Directory.CreateTempSubdirectory("gp-update-");
        try
        {
            var produced = Directory.CreateDirectory(Path.Join(directory.FullName, "produced")).FullName;
            var expected = Directory.CreateDirectory(Path.Join(directory.FullName, "expected")).FullName;
            File.WriteAllText(Path.Join(expected, "removed.json"), "{}");
            File.WriteAllText(Path.Join(expected, "golden.json"),
                "{\"artifact\":\"" + (removeGoldenArtifact ? "removed.json" : "current.json") + "\",\"sha256\":\"old\"}");
            File.WriteAllText(Path.Join(produced, "current.json"), "{\"count\":2}");

            GpCustomProjectTester.ReconcileExpectations(produced, expected, []);

            File.Exists(Path.Join(expected, "removed.json")).Should().BeFalse();
            GpCustomProjectTester.Compare(Path.Join(expected, "current.json"), Path.Join(produced, "current.json"), 0)
                .Should().BeNull();
            var goldenPath = Path.Join(expected, "golden.json");
            if (removeGoldenArtifact)
            {
                File.Exists(goldenPath).Should().BeFalse();
            }
            else
            {
                File.ReadAllText(goldenPath).Should().Contain(
                    Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Join(produced, "current.json")))));
            }
            var before = Directory.GetFiles(expected).Order(StringComparer.Ordinal).Select(File.ReadAllText).ToArray();
            GpCustomProjectTester.ReconcileExpectations(produced, expected, []);
            Directory.GetFiles(expected).Order(StringComparer.Ordinal).Select(File.ReadAllText).Should().Equal(before);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("gp-python")]
    [InlineData("gp-dotnet")]
    public void EmbeddedTemplate_MatchesItsManifest(string template)
    {
        GpCustomProjectScaffolder.VerifyEmbedded(template).Should().BeEmpty();
        GpCustomProjectScaffolder.EmbeddedFiles(template).Should().Contain("TEMPLATE-MANIFEST.json")
            .And.Contain("schema/inputs.schema.json").And.Contain("expected/golden.json").And.Contain(".vscode/launch.json").And.Contain("docker/compose.debug.yaml");
    }

    [Fact]
    public void FlatYaml_ParsesScalarsNestedMapsQuotesAndComments()
    {
        var map = GpFlatYaml.Parse("# comment\nschema: honua.gp.project/v0\nname: \"parcel-summary\"\nfixtures:\n  params: fixtures/params.json  # trailing\n  inputs:\n    features: fixtures/input.geojson\nexpected: expected\n");

        map["schema"].Should().Be("honua.gp.project/v0");
        map["name"].Should().Be("parcel-summary");
        map["fixtures.params"].Should().Be("fixtures/params.json");
        map["fixtures.inputs.features"].Should().Be("fixtures/input.geojson");
        map["expected"].Should().Be("expected");
    }

    [Theory]
    [InlineData("- item\n")]
    [InlineData("key:\n\tvalue: x\n")]
    [InlineData("key: [a, b]\nother\n")]
    public void FlatYaml_RejectsWhatItDoesNotSupport(string text)
    {
        var act = () => GpFlatYaml.Parse(text);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData(GpProjectLanguage.Python)]
    [InlineData(GpProjectLanguage.Dotnet)]
    public void Plan_CopiesTheTemplateAndAddsTheDescriptor(GpProjectLanguage language)
    {
        var template = GpCustomProjectScaffolder.TemplateFor(language);
        var plan = GpCustomProjectScaffolder.Plan(language, "parcel-summary", harnessRoot: "/srv/honua-server");

        var expectedFiles = GpCustomProjectScaffolder.EmbeddedFiles(template).Append(GpProjectManifest.FileName).ToList();
        if (language == GpProjectLanguage.Dotnet)
        {
            expectedFiles.Add("Directory.Build.rsp");
        }

        plan.Select(static f => f.RelativePath).Should().BeEquivalentTo(expectedFiles);
        foreach (var file in plan.Where(static f => f.RelativePath != GpProjectManifest.FileName && f.RelativePath != "Directory.Build.rsp"))
        {
            file.Contents.Should().Equal(GpCustomProjectScaffolder.ReadEmbedded(template, file.RelativePath), file.RelativePath + " is copied verbatim");
        }

        var descriptor = Encoding.UTF8.GetString(plan.Single(static f => f.RelativePath == GpProjectManifest.FileName).Contents);
        var shared = GpProcessDescriptor.Parse(descriptor);
        shared.Process!.Name.Should().Be("parcel-summary");
        shared.Authoring!.Template.Should().Be(template);
        shared.Authoring.TemplateCommit.Should().Be(GpCustomProjectScaffolder.TemplateCommit);
        shared.Authoring.HarnessRoot.Should().Be("/srv/honua-server");
        shared.Authoring.Fixtures!.Inputs!["features"].Should().Be("fixtures/input.geojson");
    }

    [Theory]
    [InlineData(GpProjectLanguage.Python)]
    [InlineData(GpProjectLanguage.Dotnet)]
    public void InstalledScaffold_UsesOneDescriptorForLocalTestsAndRelease(GpProjectLanguage language)
    {
        var directory = Directory.CreateTempSubdirectory("gp-installed-");
        try
        {
            var plan = GpCustomProjectScaffolder.Plan(language, "parcel-summary", null);
            GpCustomProjectScaffolder.Apply(directory.FullName, plan);
            var path = Path.Join(directory.FullName, GpProjectManifest.FileName);
            var local = GpProjectManifest.Load(path);
            var release = GpProcessDescriptor.Parse(File.ReadAllText(path));
            release.Validate().Should().BeEmpty();
            release.Process!.Name.Should().Be(local.Name);
            release.Process.Entrypoint.Should().Be(local.Entrypoint);
            release.Process.Runtime.Should().Be(language == GpProjectLanguage.Python ? "python" : "dotnet");
            release.Targets.Should().ContainSingle().Which.Kind.Should().Be("aws-batch");
            File.Exists(Path.Join(directory.FullName, release.Process.Contract!.OutputSchema!)).Should().BeTrue();
            File.Exists(Path.Join(directory.FullName, release.DependencyLock!)).Should().BeTrue();
            // Init must not fabricate remote image validation evidence.
            release.RuntimeImage.Should().BeNull();
            File.Exists(Path.Join(directory.FullName, release.Validation!.Receipts![0].Path!)).Should().BeFalse();
            plan.Should().Contain(f => f.RelativePath.EndsWith("/Honua.CustomCode.Sdk.csproj", StringComparison.Ordinal));
            plan.Should().Contain(f => f.RelativePath.EndsWith("/honua_customcode_harness/local.py", StringComparison.Ordinal));
            plan.Should().NotContain(f => f.RelativePath.Contains("/obj/", StringComparison.Ordinal) || f.RelativePath.Contains("/bin/", StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SharedDescriptor_RejectsUnknownAuthoringKeys()
    {
        var yaml = Encoding.UTF8.GetString(GpCustomProjectScaffolder.Plan(GpProjectLanguage.Python, "demo", null)
            .Single(f => f.RelativePath == GpProjectManifest.FileName).Contents);
        var act = () => GpProcessDescriptor.Parse(yaml.Replace("  expected: expected", "  misspelledExpected: expected", StringComparison.Ordinal));
        act.Should().Throw<GpReleaseException>().WithMessage("*misspelledExpected*");
    }

    [Fact]
    public void ResolveHarnessRoot_ReusesBundledToolchainOnlyWhenTheStampMatches()
    {
        var root = Directory.CreateTempSubdirectory("gp-toolchain-").FullName;
        try
        {
            var toolchain = Path.Join(root, ".honua", "toolchain");
            Directory.CreateDirectory(Path.Join(toolchain, "docker", "worker-customcode-python", "harness"));
            Directory.CreateDirectory(Path.Join(toolchain, "docker", "worker-customcode-dotnet", "harness"));

            GpCustomProjectScaffolder.ResolveHarnessRoot(null, root, GpBundledToolchain.Version).Should().BeNull();

            File.WriteAllText(Path.Join(toolchain, GpBundledToolchain.VersionFileName), "stale\n");
            GpCustomProjectScaffolder.ResolveHarnessRoot(null, root, GpBundledToolchain.Version).Should().BeNull();

            File.WriteAllText(Path.Join(toolchain, GpBundledToolchain.VersionFileName), GpBundledToolchain.Version + "\n");
            GpCustomProjectScaffolder.ResolveHarnessRoot(null, root, "other-version").Should().BeNull();
            GpCustomProjectScaffolder.ResolveHarnessRoot(null, root, GpBundledToolchain.Version)
                .Should().Be(Path.GetFullPath(toolchain));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Restore_ReplacesAStaleBundledToolchainAndKeepsAVersionMismatch()
    {
        var root = Directory.CreateTempSubdirectory("gp-restore-").FullName;
        try
        {
            var toolchain = Path.Join(root, ".honua", "toolchain");
            Directory.CreateDirectory(Path.Join(toolchain, "docker", "worker-customcode-python", "harness"));
            Directory.CreateDirectory(Path.Join(toolchain, "docker", "worker-customcode-dotnet", "harness"));
            File.WriteAllText(Path.Join(toolchain, "stale.txt"), "old");

            var mismatch = () => GpBundledToolchain.Restore(root, "not-this-version");
            mismatch.Should().Throw<InvalidDataException>();
            File.Exists(Path.Join(toolchain, "stale.txt")).Should().BeTrue();

            GpBundledToolchain.Restore(root, GpBundledToolchain.Version).Should().Be(Path.GetFullPath(toolchain));
            File.Exists(Path.Join(toolchain, "stale.txt")).Should().BeFalse();
            File.ReadAllText(Path.Join(toolchain, GpBundledToolchain.VersionFileName)).Trim()
                .Should().Be(GpBundledToolchain.Version);
            GpBundledToolchain.IsCurrent(toolchain).Should().BeTrue();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Plan_RejectsBadNames()
    {
        var act = () => GpCustomProjectScaffolder.Plan(GpProjectLanguage.Python, "Parcel Summary", null);
        act.Should().Throw<ArgumentException>();
        GpCustomProjectScaffolder.TryValidateName("parcel--summary", out _).Should().BeFalse();
        GpCustomProjectScaffolder.TryValidateName("parcel-summary-2", out _).Should().BeTrue();
    }

    [Fact]
    public void Apply_WritesOnceAndRefusesToOverwrite()
    {
        var directory = Directory.CreateTempSubdirectory("gp-project-");
        try
        {
            var plan = GpCustomProjectScaffolder.Plan(GpProjectLanguage.Python, "demo", null);
            GpCustomProjectScaffolder.Apply(directory.FullName, plan);
            File.Exists(Path.Join(directory.FullName, "process", "summary_tool.py")).Should().BeTrue();
            var manifest = GpProjectManifest.Load(Path.Join(directory.FullName, GpProjectManifest.FileName));
            manifest.HarnessRoot.Should().BeNull();
            manifest.Language.Should().Be(GpProjectLanguage.Python);

            var act = () => GpCustomProjectScaffolder.Apply(directory.FullName, plan);
            act.Should().Throw<IOException>().WithMessage("*never overwrites*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ManifestLoad_RejectsMissingKeysAndWrongSchema()
    {
        var directory = Directory.CreateTempSubdirectory("gp-manifest-");
        try
        {
            var path = Path.Join(directory.FullName, GpProjectManifest.FileName);
            File.WriteAllText(path, "schema: honua.gp.project/v9\nname: x\n");
            var act = () => GpProjectManifest.Load(path);
            act.Should().Throw<InvalidDataException>().WithMessage("*schema*");

            File.WriteAllText(path, "schema: honua.gp.project/v0\nname: x\nlanguage: dotnet\nentrypoint: A::B\nsource: process\nfixtures:\n  params: p.json\n  inputs:\n    features: f.geojson\nexpected: expected\n");
            act.Should().Throw<InvalidDataException>().WithMessage("*'build'*");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Compare_NamesTheFirstDifference()
    {
        var directory = Directory.CreateTempSubdirectory("gp-compare-");
        try
        {
            var expected = Path.Join(directory.FullName, "summary.json");
            var produced = Path.Join(directory.FullName, "produced.json");
            File.WriteAllText(expected, "{\"count\": 3, \"mean\": 20.0, \"tags\": [\"a\", \"b\"]}");

            File.WriteAllText(produced, "{\"tags\": [\"a\", \"b\"], \"mean\": 20.0000000001, \"count\": 3}");
            GpCustomProjectTester.Compare(expected, produced, 1e-9).Should().BeNull("key order and sub-tolerance noise do not matter");

            File.WriteAllText(produced, "{\"count\": 3, \"mean\": 21.0, \"tags\": [\"a\", \"b\"]}");
            GpCustomProjectTester.Compare(expected, produced, 1e-9).Should().Be("$.mean: expected 20.0, produced 21.0");

            File.WriteAllText(produced, "{\"count\": 3, \"mean\": 20.0, \"tags\": [\"a\"]}");
            GpCustomProjectTester.Compare(expected, produced, 1e-9).Should().Be("$.tags: expected 2 item(s), produced 1");

            File.WriteAllText(produced, "{\"count\": 3, \"mean\": 20.0, \"tags\": [\"a\", \"b\"], \"extra\": true}");
            GpCustomProjectTester.Compare(expected, produced, 1e-9).Should().Be("$.extra: unexpected property true");

            var expectedBin = Path.Join(directory.FullName, "a.bin");
            var producedBin = Path.Join(directory.FullName, "b.bin");
            File.WriteAllBytes(expectedBin, [1, 2, 3]);
            File.WriteAllBytes(producedBin, [1, 2, 4]);
            GpCustomProjectTester.Compare(expectedBin, producedBin, 1e-9).Should().StartWith("bytes differ");
            File.WriteAllBytes(producedBin, [1, 2, 3]);
            GpCustomProjectTester.Compare(expectedBin, producedBin, 1e-9).Should().BeNull();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
