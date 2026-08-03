// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the build and publishing contracts for the AWS Lambda Native AOT image.
/// </summary>
public sealed class LambdaAotDockerfileTests
{
    [ArchitectureTest]
    public void NightlyWorkflow_LambdaAotBuild_ForwardsDeploymentRevision()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var workflowPath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            ".github",
            "workflows",
            "nightly-container-build.yml");
        var workflow = File.ReadAllText(workflowPath);

        const string jobStart = "  build-lambda-aot:";
        const string nextJobStart = "\n  manifest-lambda-aot:";
        var start = workflow.IndexOf(jobStart, StringComparison.Ordinal);
        var end = workflow.IndexOf(nextJobStart, start, StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        var job = workflow[start..end];

        job.Should().Contain("file: docker/Dockerfile.lambda.aot");
        job.Should().Contain("HONUA_GIT_SHA=${{ github.sha }}");
    }

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
        buildStage.Should().Contain("--no-restore");
    }
}
