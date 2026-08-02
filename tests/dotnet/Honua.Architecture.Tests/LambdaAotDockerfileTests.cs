// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the RID-specific restore contract for the AWS Lambda Native AOT image.
/// </summary>
public sealed class LambdaAotDockerfileTests
{
    [ArchitectureTest]
    public void BuildStage_RestoresRidAssetsImmediatelyBeforeNoRestorePublish()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfilePath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "docker",
            "Dockerfile.lambda.aot");
        var dockerfile = File.ReadAllText(dockerfilePath);

        const string buildStageStart = "FROM restore AS build";
        const string runtimeStageStart = "FROM ${DOTNET_RUNTIME_DEPS_IMAGE} AS runtime";
        var buildStart = dockerfile.IndexOf(buildStageStart, StringComparison.Ordinal);
        var runtimeStart = dockerfile.IndexOf(runtimeStageStart, StringComparison.Ordinal);

        buildStart.Should().BeGreaterThanOrEqualTo(0);
        runtimeStart.Should().BeGreaterThan(buildStart);
        var buildStage = dockerfile[buildStart..runtimeStart];

        var retryStart = buildStage.IndexOf("until \\", StringComparison.Ordinal);
        var restore = buildStage.IndexOf(
            "sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj",
            StringComparison.Ordinal);
        var publish = buildStage.IndexOf(
            "dotnet publish src/Honua.Server/Honua.Server.csproj",
            StringComparison.Ordinal);

        retryStart.Should().BeGreaterThanOrEqualTo(0);
        restore.Should().BeGreaterThan(retryStart);
        publish.Should().BeGreaterThan(restore);
        buildStage.Should().Contain("--mount=type=secret,id=github_actor");
        buildStage.Should().Contain("--mount=type=secret,id=github_token");
        buildStage.Should().Contain("--runtime \"$RUNTIME_ID\"");
        buildStage.Should().Contain("-p:RuntimeIdentifier=\"$RUNTIME_ID\"");
        buildStage.Should().Contain("-p:HonuaUseSkiaNoDependencies=true");
        buildStage.Should().Contain("--no-restore");
    }

    [ArchitectureTest]
    public void RuntimeStage_UsesSelfContainedSkiaAndRejectsBrokenNativeLinkage()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfilePath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "docker",
            "Dockerfile.lambda.aot");
        var projectPath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "src",
            "Honua.Server",
            "Honua.Server.csproj");
        var dockerfile = File.ReadAllText(dockerfilePath);
        var project = File.ReadAllText(projectPath);

        project.Should().Contain("SkiaSharp.NativeAssets.Linux.NoDependencies");
        project.Should().Contain("'$(HonuaUseSkiaNoDependencies)' == 'true'");
        dockerfile.Should().Contain("-p:HonuaUseSkiaNoDependencies=true");
        dockerfile.Should().Contain("fonts-dejavu-core");
        dockerfile.Should().Contain("ldd -r /var/task/libSkiaSharp.so");
        dockerfile.Should().Contain("grep -Eq 'not found|undefined symbol'");
    }
}
