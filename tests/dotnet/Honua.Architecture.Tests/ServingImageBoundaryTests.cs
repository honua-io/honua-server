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

        var nightly = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/nightly-container-build.yml"));
        nightly.Should().Contain(VerifierCommand, Exactly.Twice(),
            "both generic and Lambda AOT digests must be inspected before their manifests are published");
        nightly.Should().Contain("id: build", Exactly.Twice(),
            "both AOT builds expose the immutable digest consumed by their verifier");
        nightly.Should().Contain("nightly-aot-${tag_name#nightly-}",
            "dated and SHA AOT compatibility tags must retain their established infix naming");
    }

    [ArchitectureTest]
    public void ReleaseWorkflow_ShouldVerifyAotImageBeforeRegistryPublication()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/deploy.yml"))
            .ReplaceLineEndings("\n");

        const string localBuildStep = "- name: Build native-AOT image for boundary verification";
        const string verificationStep = "- name: Verify native-AOT image before registry publication";
        const string publicationStep = "- name: Build and push";

        var localBuildIndex = workflow.IndexOf(localBuildStep, StringComparison.Ordinal);
        var verificationIndex = workflow.IndexOf(verificationStep, StringComparison.Ordinal);
        var publicationIndex = workflow.IndexOf(publicationStep, StringComparison.Ordinal);

        localBuildIndex.Should().BeGreaterThan(-1);
        verificationIndex.Should().BeGreaterThan(localBuildIndex,
            "the locally loaded AOT image must exist before it is inspected");
        publicationIndex.Should().BeGreaterThan(verificationIndex,
            "no release registry tag may be published before the AOT boundary check passes");

        var localBuild = workflow[localBuildIndex..verificationIndex];
        localBuild.Should().Contain("load: true");
        localBuild.Should().Contain("tags: honua-release-boundary:");
        localBuild.Should().NotContain("push: true");
        localBuild.Should().NotContain("steps.arch_tags.outputs.tags");
        workflow[verificationIndex..publicationIndex].Should().Contain(VerifierCommand);
    }

    [ArchitectureTest]
    public void AllOtherPublishers_ShouldVerifyAotImagesBeforeRegistryPublication()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var expectations = new[]
        {
            (
                Path: ".github/workflows/deploy-platform-images.yml",
                LocalBuild: "- name: Build local native-AOT platform image for boundary verification",
                Verification: "- name: Verify native-AOT platform image before registry publication",
                Publication: "- name: Build and push"),
            (
                Path: ".github/workflows/nightly-container-build.yml",
                LocalBuild: "- name: Build local nightly AOT image for boundary verification",
                Verification: "- name: Verify nightly AOT image before registry publication",
                Publication: "- name: Build and push"),
            (
                Path: ".github/workflows/nightly-container-build.yml",
                LocalBuild: "- name: Build local nightly Lambda AOT image for boundary verification",
                Verification: "- name: Verify nightly Lambda AOT image before registry publication",
                Publication: "- name: Build and push"),
            (
                Path: ".github/workflows/release-bundle.yml",
                LocalBuild: "- name: Build local RC AOT image for boundary verification",
                Verification: "- name: Verify RC AOT image before registry publication",
                Publication: "- name: Build & push Native AOT image (immutable RC tag)")
        };

        foreach (var expectation in expectations)
        {
            var workflow = File.ReadAllText(Path.Join(repositoryRoot, expectation.Path))
                .ReplaceLineEndings("\n");
            var localBuildIndex = workflow.IndexOf(expectation.LocalBuild, StringComparison.Ordinal);
            localBuildIndex.Should().BeGreaterThan(-1, expectation.Path);
            var verificationIndex = workflow.IndexOf(
                expectation.Verification,
                localBuildIndex,
                StringComparison.Ordinal);
            verificationIndex.Should().BeGreaterThan(localBuildIndex, expectation.Path);
            var publicationIndex = workflow.IndexOf(
                expectation.Publication,
                verificationIndex,
                StringComparison.Ordinal);
            publicationIndex.Should().BeGreaterThan(verificationIndex,
                $"{expectation.Path} must enforce the AOT boundary before publishing registry tags");

            var localBuild = workflow[localBuildIndex..verificationIndex];
            localBuild.Should().Contain("load: true", expectation.Path);
            localBuild.Should().Contain("tags: honua-", expectation.Path);
            localBuild.Should().NotContain("push: true", expectation.Path);
            localBuild.Should().NotContain("steps.meta.outputs.tags", expectation.Path);
            localBuild.Should().NotContain("steps.arch_tags.outputs.tags", expectation.Path);
            localBuild.Should().NotContain("steps.ref.outputs.image_ref", expectation.Path);
            workflow[verificationIndex..publicationIndex].Should().Contain(VerifierCommand, expectation.Path);
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
        platformDeploy.Should().Contain("publish_aot_alias: true", Exactly.Thrice());
        platformDeploy.Should().Contain("targets+=(-t \"${tag%-aot}\")");
    }

    [ArchitectureTest]
    public void RequiredPrGate_ShouldKeepBoundaryValidationLightweight()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/pr-gate.yml"));

        workflow.Should().Contain("validate-serving-image-boundary.py");
        workflow.Should().NotContain("docker/build-push-action");
        workflow.Should().NotContain("docker run -d");
    }

    [ArchitectureTest]
    public void ServingImageBoundaryWorkflow_ShouldBuildInspectAllProductionAotImagesAndSmokeCanonicalImage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/serving-image-boundary.yml"));

        workflow.Should().Contain("file: docker/Dockerfile.aot");
        workflow.Should().Contain("file: docker/Dockerfile.lambda.aot");
        workflow.Should().Contain("file: docker/Dockerfile.functions.aot");
        workflow.Should().Contain(VerifierCommand, Exactly.Thrice());
        workflow.Should().Contain("http://localhost:8080/healthz/live");
        workflow.Should().Contain("pull_request:");
        workflow.Should().Contain("paths:");

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
        workflow.Should().Contain("severity: CRITICAL,HIGH", Exactly.Twice());
        workflow.Should().Contain("ignore-unfixed: true", Exactly.Twice());
        workflow.Should().Contain("exit-code: '1'");
        workflow.Should().Contain("actions/upload-artifact");
        workflow.Should().Contain("github.event.pull_request.head.repo.fork == false");

        foreach (var transitiveInput in new[]
                 {
                     "src/Honua.Analyzers/**",
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
    public void GdalWorkerDockerfile_ShouldRestoreCompleteProjectClosureAndPinNativeSources()
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
        dockerfile.Should().Contain("ARG PDAL_SOURCE_SHA256=");
        dockerfile.Should().Contain("ARG GDAL_VERSION=3.13.1");
        dockerfile.Should().Contain("ARG DOTNET_RUNTIME_VERSION=10.0.10");
        dockerfile.Should().Contain("ubuntu-full-3.13.1");
        dockerfile.Should().Contain("apt-get -y --no-install-recommends upgrade");
        dockerfile.Should().Contain("FROM ${GDAL_BASE_IMAGE} AS pdal-build");
        dockerfile.Should().Contain("PDAL-${PDAL_VERSION}-src.tar.gz");
        dockerfile.Should().Contain("sha256sum -c -");
        dockerfile.Should().NotContain("ubuntugis");
        dockerfile.Should().NotContain(" noble main");
        dockerfile.Should().Contain("--version \"${DOTNET_RUNTIME_VERSION}\" --runtime aspnetcore");
        dockerfile.Should().Contain("gdalinfo --version | grep -F \"GDAL ${GDAL_VERSION}\"");
        dockerfile.Should().Contain("pdal --version | grep -F \"pdal ${PDAL_VERSION}\"");
        dockerfile.Should().Contain("ldd \"$(command -v pdal)\"");
        dockerfile.Should().Contain("rm -f /usr/bin/pebble");
        dockerfile.Should().Contain("! command -v pebble");
        dockerfile.Should().Contain("honua.native.gdal.version=\"${GDAL_VERSION}\"");
        dockerfile.Should().Contain("honua.native.pdal.version=\"${PDAL_VERSION}\"");
        var verifier = File.ReadAllText(Path.Join(repositoryRoot, "scripts/ci/verify-serving-image-boundary.py"));
        verifier.Should().Contain("\"honua.native.gdal.version\": \"3.13.1\"");
        verifier.Should().Contain("\"honua.native.pdal.version\": \"2.10.2\"");
        verifier.Should().Contain("\"honua.runtime.dotnet.version\": \"10.0.10\"");
        verifier.Should().Contain("worker image must run as user 1001:1001");
        verifier.Should().Contain("libpdalcpp.so");
        dockerfile.Split("--mount=type=cache,target=/root/.nuget/packages")
            .Should().HaveCount(3, "restore and publish must share the BuildKit NuGet cache");
    }
}
