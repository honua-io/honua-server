// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

public sealed class NobleRuntimeLibrdkafkaPublishTests
{
    private static readonly string[] NobleRuntimeDockerfiles =
    [
        "Dockerfile",
        "docker/Dockerfile.aot",
        "docker/Dockerfile.lambda.aot",
    ];

    private static readonly string[] OtherServerPublishDockerfiles =
    [
        "docker/Dockerfile.lambda",
        "docker/Dockerfile.lambda.aot.simple",
        "docker/Dockerfile.functions",
        "docker/Dockerfile.functions.aot",
        "docker/Dockerfile.controlplane-lambda",
    ];

    [ArchitectureTest]
    public void NobleRuntimePublishes_OmitLibrdkafkaBuildsThatCannotLoad()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var targets = File.ReadAllText(Path.Join(repositoryRoot, "Directory.Build.targets"));

        targets.Should().Contain("Name=\"RemoveUnloadedLibrdkafkaBuilds\"");
        targets.Should().Contain("Condition=\"'$(HonuaNobleRuntimePublish)' == 'true'\"");
        targets.Should().Contain("'%(Filename)' == 'alpine-librdkafka'");
        targets.Should().Contain("'$(RuntimeIdentifier)' == 'linux-x64' and '%(Extension)' == '.so' and '%(Filename)' == 'librdkafka'");

        foreach (var relativePath in NobleRuntimeDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            contents.Should().Contain("dotnet publish src/Honua.Server/Honua.Server.csproj", relativePath);
            contents.Should().Contain("-p:HonuaNobleRuntimePublish=true", relativePath);
        }

        foreach (var relativePath in OtherServerPublishDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            contents.Should().Contain("dotnet publish src/Honua.Server/Honua.Server.csproj", relativePath);
            contents.Should().NotContain("HonuaNobleRuntimePublish", relativePath);
        }
    }
}
