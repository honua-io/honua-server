// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards the Native AOT compatibility and retry contract for the Azure Functions image.
/// </summary>
public sealed class FunctionsAotDockerfileTests
{
    [ArchitectureTest]
    public void BuildStage_ExcludesUnsupportedProvidersAndRetriesSingleThreadedIlc()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var dockerfilePath = ArchitectureTestHelpers.CombinePath(
            repositoryRoot,
            "docker",
            "Dockerfile.functions.aot");
        var dockerfile = File.ReadAllText(dockerfilePath);

        dockerfile.Should().Contain(
            "-p:HonuaSkipOracleForAotVerification=true",
            Exactly.Thrice());
        dockerfile.Should().Contain(
            "-p:HonuaSkipSnowflakeForAotVerification=true",
            Exactly.Thrice());
        dockerfile.Should().Contain("-p:IlcSingleThreaded=true");
        dockerfile.Should().Contain("ARG AOT_PUBLISH_MAX_ATTEMPTS=3");
        dockerfile.Should().Contain("--mount=type=secret,id=github_actor");
        dockerfile.Should().Contain("--mount=type=secret,id=github_token");
        dockerfile.Should().Contain("until \\");
        dockerfile.Should().Contain("AOT restore+publish failed after ${attempt} attempts");
    }
}
