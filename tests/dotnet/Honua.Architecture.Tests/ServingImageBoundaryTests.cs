// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the native-AOT web image and isolated GDAL-worker distribution boundary.
/// </summary>
public sealed class ServingImageBoundaryTests
{
    private const string VerifierCommand = "scripts/ci/verify-serving-image-boundary.py --serving-image";

    [ArchitectureTest]
    public void ProductionAotDockerfiles_ShouldDeclareServingBoundaryLabels()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfiles = new Dictionary<string, string>
        {
            ["docker/Dockerfile.aot"] = "/app/Honua.Server",
            ["docker/Dockerfile.lambda.aot"] = "/var/task/Honua.Server",
            ["docker/Dockerfile.functions.aot"] = "/home/site/wwwroot/app/Honua.Server"
        };

        foreach (var (relativePath, entrypoint) in dockerfiles)
        {
            var contents = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            contents.Should().Contain("-p:PublishAot=true", relativePath);
            contents.Should().Contain("honua.runtime.profile=\"web\"", relativePath);
            contents.Should().Contain("honua.runtime.compilation=\"native-aot\"", relativePath);
            contents.Should().Contain($"honua.runtime.entrypoint=\"{entrypoint}\"", relativePath);
        }
    }

    [ArchitectureTest]
    public void ReleaseWorkflows_ShouldInspectTheAotImagesTheyPublish()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflows = new[]
        {
            ".github/workflows/release-bundle.yml",
            ".github/workflows/deploy.yml",
            ".github/workflows/deploy-platform-images.yml",
            ".github/workflows/nightly-container-build.yml"
        };

        foreach (var relativePath in workflows)
        {
            File.ReadAllText(Path.Combine(repositoryRoot, relativePath))
                .Should().Contain(VerifierCommand, relativePath);
        }
    }

    [ArchitectureTest]
    public void AuxiliaryJitDockerfiles_ShouldDeclareNonProductionProfile()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfiles = new[]
        {
            "Dockerfile",
            "docker/Dockerfile.lambda",
            "docker/Dockerfile.functions"
        };

        foreach (var relativePath in dockerfiles)
        {
            var contents = File.ReadAllText(Path.Combine(repositoryRoot, relativePath));
            contents.Should().Contain("honua.runtime.profile=\"web-debug\"", relativePath);
            contents.Should().Contain("honua.runtime.compilation=\"jit\"", relativePath);
            contents.Should().Contain("honua.runtime.distribution=\"non-production\"", relativePath);
        }
    }

    [ArchitectureTest]
    public void PublishedTags_ShouldMakeAotCanonicalAndJitExplicit()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var genericDeploy = File.ReadAllText(Path.Combine(repositoryRoot, ".github/workflows/deploy.yml"))
            .ReplaceLineEndings("\n");
        var platformDeploy = File.ReadAllText(Path.Combine(repositoryRoot, ".github/workflows/deploy-platform-images.yml"));

        genericDeploy.Should().Contain("dockerfile: Dockerfile\n            tag_suffix: -jit");
        genericDeploy.Should().Contain("dockerfile: docker/Dockerfile.aot\n            tag_suffix: \"\"");
        genericDeploy.Should().Contain("compatibility_alias: -aot");
        platformDeploy.Should().Contain("tag_suffix: -ecs-jit");
        platformDeploy.Should().Contain("tag_suffix: -lambda-jit");
        platformDeploy.Should().Contain("tag_suffix: -functions-jit");
    }

    [ArchitectureTest]
    public void RequiredPrGate_ShouldBuildInspectAndSmokeCanonicalAotImage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github/workflows/pr-gate.yml"));

        workflow.Should().Contain("file: docker/Dockerfile.aot");
        workflow.Should().Contain(VerifierCommand);
        workflow.Should().Contain("http://localhost:8080/healthz/live");
        workflow.Should().Contain("validate-serving-image-boundary.py");
    }

    [ArchitectureTest]
    public void GdalWorkerWorkflow_ShouldBuildProbeAndScanIsolatedImage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(repositoryRoot, ".github/workflows/worker-gdal-image.yml"));

        workflow.Should().Contain("file: docker/worker-gdal/Dockerfile");
        workflow.Should().Contain("--worker-image honua-worker-gdal:boundary");
        workflow.Should().Contain("aquasecurity/trivy-action");
    }
}
