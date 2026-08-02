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
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
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
            File.ReadAllText(Path.Join(repositoryRoot, relativePath))
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
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            contents.Should().Contain("honua.runtime.profile=\"web-debug\"", relativePath);
            contents.Should().Contain("honua.runtime.compilation=\"jit\"", relativePath);
            contents.Should().Contain("honua.runtime.distribution=\"non-production\"", relativePath);
        }
    }

    [ArchitectureTest]
    public void PublishedTags_ShouldMakeAotCanonicalAndJitExplicit()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var genericDeploy = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/deploy.yml"))
            .ReplaceLineEndings("\n");
        var platformDeploy = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/deploy-platform-images.yml"));

        genericDeploy.Should().Contain("dockerfile: Dockerfile\n            tag_suffix: -jit");
        genericDeploy.Should().Contain("dockerfile: docker/Dockerfile.aot\n            tag_suffix: \"\"");
        genericDeploy.Should().Contain("compatibility_alias: -aot");
        platformDeploy.Should().Contain("tag_suffix: -ecs-jit");
        platformDeploy.Should().Contain("tag_suffix: -lambda-jit");
        platformDeploy.Should().Contain("tag_suffix: -functions-jit");
    }

    [ArchitectureTest]
    public void RequiredPrGate_ShouldBuildInspectAllProductionAotImagesAndSmokeCanonicalImage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/pr-gate.yml"));

        workflow.Should().Contain("file: docker/Dockerfile.aot");
        workflow.Should().Contain("file: docker/Dockerfile.lambda.aot");
        workflow.Should().Contain("file: docker/Dockerfile.functions.aot");
        workflow.Should().Contain(VerifierCommand, Exactly.Thrice());
        workflow.Should().Contain("http://localhost:8080/healthz/live");
        workflow.Should().Contain("validate-serving-image-boundary.py");

        var functionsDockerfile = File.ReadAllText(Path.Join(repositoryRoot, "docker/Dockerfile.functions.aot"));
        foreach (var project in new[]
                 {
                     "Honua.Redshift",
                     "Honua.Snowflake",
                     "Honua.Databricks",
                     "Honua.Protocols.SensorThings"
                 })
        {
            functionsDockerfile.Should().Contain($"COPY src/{project}/*.csproj src/{project}/");
        }

        foreach (var transitiveInput in new[]
                 {
                     "src/**",
                     "docs/gis/data/feature-catalog.json",
                     "docs/developer/api-specs/admin-api.json",
                     "tests/fixtures/ai-builder/**",
                     "Directory.Build.props",
                     "Directory.Build.targets",
                     "Directory.Packages.props",
                     "eng/**",
                     "scripts/docker/restore-dotnet-with-github-packages.sh",
                     ".dockerignore"
                 })
        {
            workflow.Should().Contain(transitiveInput);
        }
    }

    [ArchitectureTest]
    public void GdalWorkerWorkflow_ShouldBuildSmokeProbeScanAndRetainReport()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/worker-gdal-image.yml"));

        workflow.Should().Contain("file: docker/worker-gdal/Dockerfile");
        workflow.Should().Contain("--worker-image honua-worker-gdal:boundary");
        workflow.Should().Contain("--worker-redis 127.0.0.1:6379");
        workflow.Should().Contain("redis:");
        workflow.Should().Contain("aquasecurity/trivy-action");
        workflow.Should().Contain("actions/upload-artifact");
        workflow.Should().Contain("github.event.pull_request.head.repo.fork == false");

        foreach (var transitiveInput in new[]
                 {
                     "src/Honua.Core.Abstractions/**",
                     "src/Honua.Hosting/**",
                     "src/Honua.ServiceDefaults/**",
                     "src/Honua.Geometry/**",
                     "Directory.Build.targets",
                     "eng/**",
                     "scripts/docker/restore-dotnet-with-github-packages.sh",
                     ".dockerignore"
                 })
        {
            workflow.Should().Contain(transitiveInput);
        }
    }

    [ArchitectureTest]
    public void GdalWorkerDockerfile_ShouldRestoreCompleteProjectClosureAndPinPdalSource()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Join(repositoryRoot, "docker/worker-gdal/Dockerfile"));

        foreach (var project in new[]
                 {
                     "Honua.Core.Abstractions",
                     "Honua.Core",
                     "Honua.Geometry",
                     "Honua.Hosting",
                     "Honua.ServiceDefaults",
                     "Honua.Jobs",
                     "Honua.Worker.Gdal"
                 })
        {
            dockerfile.Should().Contain($"COPY src/{project}/*.csproj src/{project}/");
        }

        dockerfile.Should().Contain("ARG PDAL_VERSION=");
        dockerfile.Should().Contain("ARG GDAL_BASE_IMAGE=ghcr.io/osgeo/gdal:ubuntu-full-3.13.1");
        dockerfile.Should().Contain("libssl3t64 openssl");
        dockerfile.Should().Contain("pdal=\"${PDAL_VERSION}\"");
        dockerfile.Should().Contain("libpdal-plugins=\"${PDAL_VERSION}\"");
        dockerfile.Should().Contain("signed-by=/usr/share/keyrings/ubuntugis-archive-keyring.gpg");
        dockerfile.Should().Contain("--runtime aspnetcore --install-dir /usr/share/dotnet");
        dockerfile.Split("--mount=type=cache,target=/root/.nuget/packages")
            .Should().HaveCount(3, "restore and publish must share the BuildKit NuGet cache");
    }
}
