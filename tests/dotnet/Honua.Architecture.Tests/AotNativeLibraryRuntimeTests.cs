// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the published native-AOT images against shipping native libraries they cannot load
/// (honua-server#4912). ParquetSharp (the GeoParquet <c>f=parquet</c> encoder) and DuckDB ship
/// only glibc native assets. The canonical AOT image was published as <c>linux-musl-x64</c> on
/// Alpine, so the publish copied the glibc <c>ParquetSharpNative.so</c> and every
/// <c>f=parquet</c> request answered 501. The Lambda image was glibc but lacked
/// <c>libatomic.so.1</c>, with the same result. The boundary check, unit lanes and the CNG
/// branch lane (which builds the JIT image) all passed on that shape, so these assertions pin
/// the image shape and the build-lane smoke that exercises the encoder.
/// </summary>
public sealed class AotNativeLibraryRuntimeTests
{
    private static readonly string[] _publishedServerAotDockerfiles =
    [
        "docker/Dockerfile.aot",
        "docker/Dockerfile.lambda.aot"
    ];

    [ArchitectureTest]
    public void PublishedServerAotDockerfiles_ShouldTargetGlibcAndInstallParquetNativeDependencies()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        File.ReadAllText(Path.Join(repositoryRoot, "src/Honua.Server/Honua.Server.csproj"))
            .Should().Contain("<PackageReference Include=\"ParquetSharp\" />",
                "this guard exists because the server graph ships the glibc-only ParquetSharpNative");

        foreach (var relativePath in _publishedServerAotDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));

            contents.Should().NotContain("linux-musl-",
                $"{relativePath}: ParquetSharp and DuckDB have no musl native asset, so a musl publish "
                + "ships glibc libraries that cannot load (f=parquet answered 501 on pin 548b7a5)");
            contents.Should().NotContain("-alpine@sha256:",
                $"{relativePath}: gcompat on Alpine loads ParquetSharpNative but segfaults the server");
            contents.Should().NotContain("apk add", relativePath);
            contents.Should().Contain("amd64) RUNTIME_ID=\"linux-x64\"", relativePath);
            contents.Should().Contain("arm64) RUNTIME_ID=\"linux-arm64\"", relativePath);
            contents.Should().Contain("libatomic1",
                $"{relativePath}: ParquetSharpNative.so links libatomic.so.1, which runtime-deps does not ship");
        }
    }

    [ArchitectureTest]
    public void PublishedServerAotDockerfiles_ShouldFailTheBuildWhenNativeLibrariesCannotLink()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();

        foreach (var relativePath in _publishedServerAotDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            var runtimeStage = contents[contents.IndexOf("AS runtime", StringComparison.Ordinal)..];

            runtimeStage.Should().MatchRegex(@"for native in [^;]*\bParquetSharpNative\.so\b[^;]*\blibduckdb\.so\b|for native in [^;]*\blibduckdb\.so\b[^;]*\bParquetSharpNative\.so\b",
                $"{relativePath} must link-check both glibc-only native libraries in the runtime stage");
            runtimeStage.Should().Contain("ldd -r \"", relativePath);
            runtimeStage.Should().Contain("grep -Eq 'not found|undefined symbol|not a dynamic executable'", relativePath);
            runtimeStage.Should().Contain("exit 1", relativePath);
        }
    }

    [ArchitectureTest]
    public void NightlyAotBuild_ShouldSmokeGeoParquetOnTheCandidateDigestBeforePublishingTags()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/nightly-container-build.yml"))
            .ReplaceLineEndings("\n");

        var verificationIndex = workflow.IndexOf(
            "- name: Verify nightly AOT candidate before registry publication", StringComparison.Ordinal);
        var smokeIndex = workflow.IndexOf(
            "- name: Smoke nightly AOT candidate GeoParquet encoder before registry publication", StringComparison.Ordinal);
        var publicationIndex = workflow.IndexOf(
            "- name: Publish verified nightly AOT architecture tags", StringComparison.Ordinal);

        verificationIndex.Should().BeGreaterThan(-1);
        smokeIndex.Should().BeGreaterThan(verificationIndex);
        publicationIndex.Should().BeGreaterThan(smokeIndex,
            "the GeoParquet smoke must gate publication of the nightly AOT tags");

        var smoke = workflow[smokeIndex..publicationIndex];
        smoke.Should().Contain("steps.build.outputs.digest", "the smoke must boot the exact candidate digest");
        smoke.Should().Contain("scripts/ci/smoke-aot-geoparquet.sh \"$CANDIDATE\"");
        smoke.Should().NotContain("continue-on-error");
        smoke.Should().Contain("if: matrix.arch == 'amd64'",
            "the CNG seed database image is amd64-only; any other condition would silently skip the smoke");
        smoke.Should().NotContain("if: ${{", "the smoke must not gain an escape-hatch condition");

        var script = File.ReadAllText(Path.Join(repositoryRoot, "scripts/ci/smoke-aot-geoparquet.sh"));
        script.Should().Contain("f=parquet");
        script.Should().Contain("[[ \"$code\" == 200 ]]");
        script.Should().Contain("PAR1", "a 200 carrying the 501 capability envelope must not pass");
        script.Should().Contain("docker/cng/seed.sql");
    }

    [ArchitectureTest]
    public void BaseImageMirrors_ShouldNotMirrorAlpineBasesForTheCanonicalAotImage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var mirrors = File.ReadAllText(Path.Join(repositoryRoot, "scripts/ci/base-image-mirrors.sh"));
        var nightly = File.ReadAllText(Path.Join(repositoryRoot, ".github/workflows/nightly-container-build.yml"));

        mirrors.Should().Contain("\"sdk-10.0-aot|docker/Dockerfile.aot|DOTNET_SDK_IMAGE\"");
        mirrors.Should().Contain("\"runtime-deps-10.0-aot|docker/Dockerfile.aot|DOTNET_RUNTIME_DEPS_IMAGE\"");
        nightly.Should().Contain("DOTNET_SDK_IMAGE=${{ env.BASE_REPO }}:sdk-10.0-aot");
        nightly.Should().Contain("DOTNET_RUNTIME_DEPS_IMAGE=${{ env.BASE_REPO }}:runtime-deps-10.0-aot");
        nightly.Should().NotContain("10.0-alpine");
    }
}
