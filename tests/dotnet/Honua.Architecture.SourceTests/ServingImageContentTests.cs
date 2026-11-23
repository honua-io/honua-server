// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

// docker/metadata-action adds a default OCI licence label from the GitHub repository licence,
// which GitHub reports as NOASSERTION for FCL-1.0-ALv2; build labels override Dockerfile labels.
public sealed partial class ServingImageContentTests
{
    private const string LicenceValue = "org.opencontainers.image.licenses=LicenseRef-FCL-1.0-ALv2";
    private const string StepSeparator = "\n      - ";

    private static readonly string[] ServingDockerfiles = ["Dockerfile", "docker/Dockerfile.aot"];

    [ArchitectureTest]
    public void BuildsFedByMetadataAction_LabelTheFclLicence()
    {
        var offenders = ReadWorkflows()
            .SelectMany(workflow => JobBoundary().Split(workflow.Content).Select(job => (workflow.Name, Steps: job.Split(StepSeparator))))
            .SelectMany(job => job.Steps
                .Select(step => MetadataLabelsConsumer().Match(step))
                .Where(consumer => consumer.Success)
                .Select(consumer => (job.Name, job.Steps, Id: consumer.Groups["id"].Value)))
            .Where(use => !MetadataStepLabelsLicence(use.Steps, use.Id))
            .Select(use => $"{use.Name}: metadata step '{use.Id}'")
            .ToList();

        offenders.Should().BeEmpty("a build fed by metadata-action labels must override its NOASSERTION licence label");
    }

    [ArchitectureTest]
    public void NightlyServingAndExecutorImages_CarryTheFclLicenceOnEveryLevel()
    {
        var workflow = File.ReadAllText(WorkflowPath("nightly-container-build.yml"));
        var indexAnnotations = MultiArchManifestCommand().Matches(workflow);
        indexAnnotations.Should().NotBeEmpty();
        indexAnnotations.Select(command => command.Value)
            .Should().OnlyContain(command => command.Contains($"--annotation \"index:{LicenceValue}\"", StringComparison.Ordinal),
                "GHCR shows multi-arch package metadata from index annotations");

        var executor = workflow.Split(StepSeparator).Single(step => step.Contains("file: docker/Dockerfile.compose-executor", StringComparison.Ordinal));
        BlockLines(executor, "labels").Should().Contain(LicenceValue);
        BlockLines(executor, "annotations").Should().Contain($"index:{LicenceValue}");
    }

    [ArchitectureTest]
    public void ServingDockerfiles_CarryCurrentOciLabelsOnly()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        foreach (var path in ServingDockerfiles)
        {
            var dockerfile = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, path));

            dockerfile.Should().Contain("org.opencontainers.image.licenses=\"LicenseRef-FCL-1.0-ALv2\"", path);
            dockerfile.Should().Contain("org.opencontainers.image.source=\"https://github.com/honua-io/honua-server\"", path);
            dockerfile.Should().NotContain("version=\"1.0\"", $"{path} versions come from org.opencontainers.image.version");
            dockerfile.Should().NotContain("maintainer=", path);
        }
    }

    [ArchitectureTest]
    public void FeatureCatalog_IsEmbeddedInOneAssemblyOnly()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var embeddingProjects = Directory
            .EnumerateFiles(ArchitectureTestHelpers.CombinePath(root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(project => File.ReadAllText(project).Contains("feature-catalog.json\"", StringComparison.Ordinal))
            .Select(project => Path.GetFileName(project))
            .ToList();

        embeddingProjects.Should().Equal(["Honua.Ai.csproj"], "the 2 MB catalog ships once in the serving binary");
    }

    // The uncommented entries of a "key: |" block, or the single value of "key: value", in one workflow step.
    private static List<string> BlockLines(string step, string key)
    {
        var lines = step.Split('\n');
        var header = Array.FindIndex(lines, line => line.TrimStart().StartsWith($"{key}:", StringComparison.Ordinal));
        if (header < 0)
        {
            return [];
        }

        var inline = lines[header].TrimStart()[(key.Length + 1)..].Trim();
        if (inline is not ("|" or "|-" or ">" or ">-"))
        {
            return [inline];
        }

        var indent = lines[header].Length - lines[header].TrimStart().Length;
        return lines.Skip(header + 1)
            .TakeWhile(line => line.Trim().Length == 0 || line.Length - line.TrimStart().Length > indent)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToList();
    }

    private static bool MetadataStepLabelsLicence(string[] steps, string stepId) =>
        steps.FirstOrDefault(step => step.Contains($"\n        id: {stepId}\n", StringComparison.Ordinal)) is { } metadata
        && BlockLines(metadata, "labels").Contains(LicenceValue);

    private static IEnumerable<(string Name, string Content)> ReadWorkflows() =>
        Directory.EnumerateFiles(Path.GetDirectoryName(WorkflowPath("nightly-container-build.yml"))!, "*.yml")
            .Select(path => (Path.GetFileName(path), File.ReadAllText(path)));

    private static string WorkflowPath(string name) =>
        ArchitectureTestHelpers.CombinePath(ArchitectureTestHelpers.ResolveRepositoryRoot(), ".github", "workflows", name);

    [GeneratedRegex(@"\n\s+labels: \$\{\{ steps\.(?<id>[A-Za-z0-9_-]+)\.outputs\.labels \}\}")]
    private static partial Regex MetadataLabelsConsumer();

    [GeneratedRegex(@"\n(?=  [A-Za-z0-9_-]+:[ \t]*\r?\n)")]
    private static partial Regex JobBoundary();

    // Multi-line "imagetools create" commands publish the nightly multi-arch indexes; single-line ones mirror base images.
    [GeneratedRegex(@"docker buildx imagetools create(?:[^\n]*\\\n)+[^\n]*\n")]
    private static partial Regex MultiArchManifestCommand();
}
