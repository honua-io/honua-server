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
    public void Publishers_ShouldPromoteTheExactVerifiedAotDigest()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var expectations = new[]
        {
            (
                Path: ".github/workflows/deploy.yml",
                Candidate: "- name: Build native-AOT candidate by digest",
                Verification: "- name: Verify native-AOT candidate before registry publication",
                Publication: "- name: Publish verified native-AOT architecture tags",
                Digest: "steps.aot_candidate.outputs.digest",
                CrossRegistry: true),
            (
                Path: ".github/workflows/deploy-platform-images.yml",
                Candidate: "- name: Build native-AOT platform candidate by digest",
                Verification: "- name: Verify native-AOT platform candidate before registry publication",
                Publication: "- name: Publish verified native-AOT platform architecture tags",
                Digest: "steps.aot_candidate.outputs.digest",
                CrossRegistry: true),
            (
                Path: ".github/workflows/nightly-container-build.yml",
                Candidate: "- name: Build nightly AOT candidate by digest",
                Verification: "- name: Verify nightly AOT candidate before registry publication",
                Publication: "- name: Publish verified nightly AOT architecture tags",
                Digest: "steps.build.outputs.digest",
                CrossRegistry: true),
            (
                Path: ".github/workflows/nightly-container-build.yml",
                Candidate: "- name: Build nightly Lambda AOT candidate by digest",
                Verification: "- name: Verify nightly Lambda AOT candidate before registry publication",
                Publication: "- name: Publish verified nightly Lambda AOT architecture tags",
                Digest: "steps.build.outputs.digest",
                CrossRegistry: true),
            (
                Path: ".github/workflows/release-bundle.yml",
                Candidate: "- name: Build RC AOT candidate by digest",
                Verification: "- name: Verify RC AOT candidate before registry publication",
                Publication: "- name: Publish verified RC AOT tag",
                Digest: "steps.build.outputs.digest",
                CrossRegistry: false)
        };

        foreach (var expectation in expectations)
        {
            var workflow = File.ReadAllText(Path.Join(repositoryRoot, expectation.Path))
                .ReplaceLineEndings("\n");
            var candidateIndex = workflow.IndexOf(expectation.Candidate, StringComparison.Ordinal);
            candidateIndex.Should().BeGreaterThan(-1, expectation.Path);
            var verificationIndex = workflow.IndexOf(
                expectation.Verification,
                candidateIndex,
                StringComparison.Ordinal);
            verificationIndex.Should().BeGreaterThan(candidateIndex, expectation.Path);
            var publicationIndex = workflow.IndexOf(
                expectation.Publication,
                verificationIndex,
                StringComparison.Ordinal);
            publicationIndex.Should().BeGreaterThan(verificationIndex,
                $"{expectation.Path} must enforce the AOT boundary before publishing registry tags");

            var nextStepIndex = workflow.IndexOf(
                "\n      - name:",
                publicationIndex + expectation.Publication.Length,
                StringComparison.Ordinal);
            var publicationEnd = nextStepIndex >= 0 ? nextStepIndex : workflow.Length;
            var candidate = workflow[candidateIndex..verificationIndex];
            var verification = workflow[verificationIndex..publicationIndex];
            var publication = workflow[publicationIndex..publicationEnd];

            candidate.Should().Contain("push-by-digest=true", expectation.Path);
            candidate.Should().Contain("name-canonical=true", expectation.Path);
            candidate.Should().NotContain("tags:", expectation.Path);
            candidate.Should().NotContain("steps.meta.outputs.tags", expectation.Path);
            candidate.Should().NotContain("steps.arch_tags.outputs.tags", expectation.Path);
            candidate.Should().NotContain("steps.ref.outputs.image_ref", expectation.Path);
            workflow[candidateIndex..publicationIndex].Should().Contain(
                "docker/build-push-action",
                Exactly.Once(),
                $"{expectation.Path} must not rebuild between candidate creation and verification");
            verification.Should().Contain(VerifierCommand, expectation.Path);
            verification.Should().Contain(expectation.Digest, expectation.Path);
            publication.Should().Contain(expectation.Digest,
                $"{expectation.Path} must promote the digest that passed verification");
            if (expectation.CrossRegistry)
            {
                publication.Should().Contain("scripts/ci/promote-verified-image.py", expectation.Path);
                publication.Should().Contain("--staging-tag",
                    $"{expectation.Path} must verify every target registry before assigning public tags");
            }
            else
            {
                publication.Should().Contain("docker buildx imagetools create", expectation.Path);
                publication.Should().Contain("--prefer-index=false",
                    $"{expectation.Path} must preserve the verified candidate manifest rather than wrap it");
            }
        }

        var crossRegistryPromotion = File.ReadAllText(
            Path.Join(repositoryRoot, "scripts/ci/promote-verified-image.py"));
        crossRegistryPromotion.Should().Contain("--all");
        crossRegistryPromotion.Should().Contain("--preserve-digests");
        crossRegistryPromotion.Should().Contain("client.raw_manifest(staged)");
        crossRegistryPromotion.Should().Contain("actual_digest != expected_digest");
    }

    [ArchitectureTest]
    public void ReleaseBundle_ShouldReserveLatestTagsForGaPromotion()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/release-bundle.yml"))
            .ReplaceLineEndings("\n");

        var channelTagsIndex = workflow.IndexOf(
            "--tag \"${REGISTRY_GHCR}/${IMAGE_NAME}:${channel}\"",
            StringComparison.Ordinal);
        var gaGuardIndex = workflow.IndexOf("if [[ \"$channel\" == \"ga\" ]]; then", StringComparison.Ordinal);
        var latestTagIndex = workflow.IndexOf(
            "--tag \"${REGISTRY_GHCR}/${IMAGE_NAME}:latest\"",
            StringComparison.Ordinal);
        var publicationIndex = workflow.IndexOf(
            "docker buildx imagetools create --prefer-index=false \"${targets[@]}\"",
            StringComparison.Ordinal);

        channelTagsIndex.Should().BeGreaterThan(-1,
            "preview, RC, and GA promotions must retain their channel-specific tags");
        gaGuardIndex.Should().BeGreaterThan(channelTagsIndex);
        latestTagIndex.Should().BeGreaterThan(gaGuardIndex,
            "mutable latest tags must only be collected inside the GA guard");
        publicationIndex.Should().BeGreaterThan(latestTagIndex);
        workflow[gaGuardIndex..publicationIndex].Should().Contain(":latest-aot\"");

        var documentation = File.ReadAllText(
            Path.Join(repositoryRoot, "docs/internal/contributor/release-bundle.md"));
        documentation.Should().Contain("It advances `latest` and `latest-aot` only");
        documentation.Should().Contain("preview and RC promotions do not move the stable tags");
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
        genericDeploy.Should().Contain("- name: Classify mutable latest promotion", Exactly.Twice());
        genericDeploy.Should().Contain(
            "type=raw,value=latest${{ matrix.tag_suffix }},enable=${{ steps.release_kind.outputs.promote_latest }}",
            Exactly.Twice());
        genericDeploy.Should().Contain("            latest=false", Exactly.Twice(),
            "only the explicit promotion classifier may create mutable latest aliases");
        genericDeploy.Should().Contain(
            "\"$RELEASE_TAG\" =~ ^v[0-9]+\\.[0-9]+\\.[0-9]+(\\+[0-9A-Za-z.-]+)?$",
            Exactly.Twice(),
            "only stable semantic-version tags may advance latest and latest-jit");
        genericDeploy.Should().Contain(
            "\"$GITHUB_REF_TYPE\" == \"branch\" && \"$RELEASE_TAG\" == \"$DEFAULT_BRANCH\"",
            Exactly.Twice(),
            "explicit default-branch dispatches must preserve their existing latest behavior");
        genericDeploy.Should().NotContain("enable={{is_default_branch}}",
            "a v* tag ref is never the default branch and would silently leave latest stale");
        genericDeploy.Should().NotContain(
            "type=raw,value=latest${{ matrix.tag_suffix }},enable=${{ startsWith(github.ref, 'refs/tags/v')",
            "preview and RC v* tags must not advance stable latest aliases");
        genericDeploy.Should().Contain("needs: build-and-push",
            "multi-architecture latest aliases must wait for verified architecture publications");

        var stableReleasePattern = new System.Text.RegularExpressions.Regex(
            @"^v[0-9]+\.[0-9]+\.[0-9]+(\+[0-9A-Za-z.-]+)?$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        foreach (var stableTag in new[] { "v1.2.3", "v10.20.30+build-1" })
        {
            stableReleasePattern.IsMatch(stableTag).Should().BeTrue(stableTag);
        }

        foreach (var prereleaseTag in new[] { "v1.2.3-preview.4", "v1.2.3-rc.1", "trunk" })
        {
            stableReleasePattern.IsMatch(prereleaseTag).Should().BeFalse(prereleaseTag);
        }

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
        workflow.Should().Contain("validate-promote-verified-image.py");
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
        functionsDockerfile.Should().Contain("-p:HonuaSkipOracleForAotVerification=true", Exactly.Thrice());
        functionsDockerfile.Should().Contain("-p:HonuaSkipSnowflakeForAotVerification=true", Exactly.Thrice());
        functionsDockerfile.Should().Contain("-p:IlcSingleThreaded=true");
        functionsDockerfile.Should().Contain("ARG AOT_PUBLISH_MAX_ATTEMPTS=3");
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
        const string lockedNugetMount = "--mount=type=cache,target=/root/.nuget/packages,sharing=locked";
        dockerfile.Split(lockedNugetMount)
            .Should().HaveCount(3, "restore and publish must share the same locked BuildKit NuGet cache");

        var fullSourceIndex = dockerfile.IndexOf("COPY . .", StringComparison.Ordinal);
        var nativeBuildIndex = dockerfile.IndexOf("FROM ${GDAL_BASE_IMAGE} AS pdal-build", StringComparison.Ordinal);
        fullSourceIndex.Should().BeGreaterThan(-1);
        nativeBuildIndex.Should().BeGreaterThan(fullSourceIndex);
        var finalManagedBuild = dockerfile[fullSourceIndex..nativeBuildIndex];
        var authenticatedRestoreIndex = finalManagedBuild.IndexOf(
            "sh scripts/docker/restore-dotnet-with-github-packages.sh",
            StringComparison.Ordinal);
        var publishIndex = finalManagedBuild.IndexOf("dotnet publish", StringComparison.Ordinal);

        finalManagedBuild.Should().Contain(lockedNugetMount);
        finalManagedBuild.Should().Contain("--mount=type=secret,id=github_actor");
        finalManagedBuild.Should().Contain("--mount=type=secret,id=github_token");
        authenticatedRestoreIndex.Should().BeGreaterThan(-1);
        publishIndex.Should().BeGreaterThan(authenticatedRestoreIndex,
            "the authenticated restore must populate the exact cache mount consumed by publish");
        dockerfile.Should().NotContain("ARG GITHUB_TOKEN");
        dockerfile.Should().NotContain("ENV GITHUB_TOKEN");
    }
}
