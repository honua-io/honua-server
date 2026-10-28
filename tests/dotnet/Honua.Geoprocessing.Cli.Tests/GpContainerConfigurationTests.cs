// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Xunit;

namespace Honua.Geoprocessing.Cli.Tests;

[global::Xunit.Trait("Tier", "Fast")]
public sealed class GpContainerConfigurationTests
{
    [Fact]
    public void Defaults_UseInvokingIdentityForWritableDeveloperMounts()
    {
        var configuration = GpContainerConfiguration.Create(_ => null);
        configuration["GdalContainer:User"].Should().Be(GpContainerConfiguration.CurrentUser());
        configuration["GdalContainer:DockerExecutable"].Should().Be("docker");
    }

    [Fact]
    public void Overrides_ArePassedToBothImageProbeAndExecutor()
    {
        var settings = new Dictionary<string, string?>
        {
            ["HONUA_GP_WORKER_IMAGE"] = "worker@sha256:abc",
            ["HONUA_GP_CONTAINER_RUNTIME"] = "podman",
            ["HONUA_GP_CONTAINER_USER"] = "1234:5678",
        };
        var configuration = GpContainerConfiguration.Create(settings.GetValueOrDefault);
        configuration["GdalContainer:Image"].Should().Be("worker@sha256:abc");
        configuration["GdalContainer:DockerExecutable"].Should().Be("podman");
        configuration["GdalContainer:User"].Should().Be("1234:5678");
        configuration["Geoprocessing:Executors:OutputRootDirectory"].Should().BeNull();
    }

    [Fact]
    public void ScratchRoot_RedirectsExecutorOutputsUnderThatRoot()
    {
        var scratch = Path.Join(Path.GetTempPath(), "honua-gp-scratch-" + Guid.NewGuid().ToString("N"));
        var configuration = GpContainerConfiguration.Create(_ => null, scratchRoot: scratch);

        var outputRoot = configuration["Geoprocessing:Executors:OutputRootDirectory"];
        outputRoot.Should().Be(Path.Join(scratch, "executor-output"));
        configuration["GdalWorker:ScratchRoot"].Should().Be(scratch);
    }
}
