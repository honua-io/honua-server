// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;

namespace Honua.Architecture.Tests;

public sealed class UbuntuRuntimeLibrdkafkaPublishTests
{
    private static readonly string[] UbuntuRuntimeDockerfiles =
    [
        "Dockerfile",
        "docker/Dockerfile.aot",
        "docker/Dockerfile.lambda.aot",
    ];

    private static readonly string[] OtherServerPublishDockerfiles =
    [
        "docker/Dockerfile.lambda",
        "docker/Dockerfile.functions",
        "docker/Dockerfile.functions.aot",
    ];

    [ArchitectureTest]
    public void UbuntuRuntimePublishes_OmitLibrdkafkaBuildsThatCannotLoad()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var targets = File.ReadAllText(Path.Join(repositoryRoot, "Directory.Build.targets"));

        targets.Should().Contain("Name=\"RemoveUnloadedLibrdkafkaBuilds\"");
        targets.Should().Contain("Condition=\"'$(HonuaUbuntuRuntimePublish)' == 'true'\"");
        targets.Should().Contain("'%(Filename)' == 'alpine-librdkafka'");
        targets.Should().Contain("'$(RuntimeIdentifier)' == 'linux-x64' and '%(Extension)' == '.so' and '%(Filename)' == 'librdkafka'");

        foreach (var relativePath in UbuntuRuntimeDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            contents.Should().Contain("dotnet publish src/Honua.Server/Honua.Server.csproj", relativePath);
            contents.Should().Contain("-p:HonuaUbuntuRuntimePublish=true", relativePath);
        }

        foreach (var relativePath in OtherServerPublishDockerfiles)
        {
            var contents = File.ReadAllText(Path.Join(repositoryRoot, relativePath));
            contents.Should().Contain("dotnet publish src/Honua.Server/Honua.Server.csproj", relativePath);
            contents.Should().NotContain("HonuaUbuntuRuntimePublish", relativePath);
        }
    }
}
