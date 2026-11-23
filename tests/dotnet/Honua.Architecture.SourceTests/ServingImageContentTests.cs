// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

// docker/metadata-action adds a default OCI licence label from the GitHub repository licence,
// which GitHub reports as NOASSERTION for FCL-1.0-ALv2; build labels override Dockerfile labels.
public sealed class ServingImageContentTests
{
    private const string LicenceLabel = "org.opencontainers.image.licenses=LicenseRef-FCL-1.0-ALv2";

    private static readonly string[] ImageWorkflows =
    [
        "nightly-container-build.yml",
        "deploy.yml",
        "deploy-platform-images.yml",
    ];

    private static readonly string[] ServingDockerfiles = ["Dockerfile", "docker/Dockerfile.aot"];

    [ArchitectureTest]
    public void ImageWorkflows_EveryMetadataAndLabelledBuildStep_SetsTheFclLicence()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var offenders = new List<string>();
        foreach (var name in ImageWorkflows)
        {
            var workflow = File.ReadAllText(ArchitectureTestHelpers.CombinePath(root, ".github", "workflows", name));
            var steps = workflow.Split("\n      - ", StringSplitOptions.None);
            offenders.AddRange(steps
                .Where(IsImageMetadataStep)
                .Where(step => !step.Contains(LicenceLabel, StringComparison.Ordinal))
                .Select(step => $"{name}: {step.Split('\n')[0]}"));
        }

        offenders.Should().BeEmpty("every published image must carry the FCL licence, not NOASSERTION");
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

    private static bool IsImageMetadataStep(string step) =>
        step.Contains("docker/metadata-action@", StringComparison.Ordinal)
        || (step.Contains("docker/build-push-action@", StringComparison.Ordinal)
            && step.Contains("labels: |", StringComparison.Ordinal));
}
