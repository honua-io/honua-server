// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the RID-specific restore contract for the AWS Lambda JIT image.
/// </summary>
public sealed class LambdaJitDockerfileTests
{
    [ArchitectureTest]
    public void SourceLayer_ForcesExactRidRestoreImmediatelyBeforeNoRestorePublish()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfilePath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "docker",
            "Dockerfile.lambda");
        var dockerfile = File.ReadAllText(dockerfilePath);

        const string sourceLayerStart = "COPY . .";
        const string runtimeStageStart = "FROM ${LAMBDA_RUNTIME_IMAGE} AS runtime";
        var sourceStart = dockerfile.IndexOf(sourceLayerStart, StringComparison.Ordinal);
        var runtimeStart = dockerfile.IndexOf(runtimeStageStart, StringComparison.Ordinal);

        sourceStart.Should().BeGreaterThanOrEqualTo(0);
        runtimeStart.Should().BeGreaterThan(sourceStart);
        var sourceLayer = dockerfile[sourceStart..runtimeStart];

        var restore = sourceLayer.IndexOf(
            "sh scripts/docker/restore-dotnet-with-github-packages.sh src/Honua.Server/Honua.Server.csproj",
            StringComparison.Ordinal);
        var publish = sourceLayer.IndexOf(
            "dotnet publish src/Honua.Server/Honua.Server.csproj",
            StringComparison.Ordinal);

        restore.Should().BeGreaterThanOrEqualTo(0);
        publish.Should().BeGreaterThan(restore);
        sourceLayer.Should().Contain("--mount=type=secret,id=github_actor");
        sourceLayer.Should().Contain("--mount=type=secret,id=github_token");
        sourceLayer.Should().Contain("--runtime \"$RUNTIME_ID\"");
        sourceLayer.Should().Contain("--force");
        sourceLayer.Should().Contain("-p:RuntimeIdentifier=\"$RUNTIME_ID\"");
        sourceLayer.Should().Contain("-p:HonuaIncludeStacOpsDemo=false");
        sourceLayer.Should().Contain("-p:PublishAot=false");
        sourceLayer.Should().Contain("-p:SelfContained=true");
        sourceLayer.Should().Contain("--no-restore");
    }
}
