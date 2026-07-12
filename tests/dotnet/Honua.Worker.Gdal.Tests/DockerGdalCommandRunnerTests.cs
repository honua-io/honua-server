// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Offline coverage for the opt-in container-exec GDAL command runner
/// (<see cref="DockerGdalCommandRunner"/>, issue #2180). Docker is NOT available on a
/// lean CI agent, so these tests verify the load-bearing, correct-by-construction part
/// of the runner — the exact <c>docker run …</c> argument vector it builds (image ref,
/// identical-path bind mount, working dir, user, network, entrypoint-override, tool
/// args) — and that it delegates to the container-runtime seam. The end-to-end run
/// against a real container is CI/local-Docker-verified (it needs the
/// <c>honua-worker-etl</c> image and a Docker daemon).
/// </summary>
public sealed class DockerGdalCommandRunnerTests
{
    [UnitTest]
    public void BuildDockerRunArguments_BindMountsWorkspaceAtIdenticalPath_AndOverridesEntrypoint()
    {
        var options = new GdalContainerExecutionOptions();
        const string workspace = "/tmp/honua-gdal-worker/op-123";
        var toolArgs = new[] { "hillshade", "-of", "GTiff", "/tmp/honua-gdal-worker/op-123/input.tif", "/tmp/honua-gdal-worker/op-123/output.tif" };

        var args = DockerGdalCommandRunner.BuildDockerRunArguments(options, "gdaldem", toolArgs, workspace);

        // docker run --rm --network none --user 1001:1001 -v <ws>:<ws> -w <ws>
        //   --entrypoint gdaldem honua-worker-etl <toolArgs...>
        args.Should().ContainInOrder("run", "--rm");
        args.Should().ContainInOrder("--network", "none");
        args.Should().ContainInOrder("--user", "1001:1001");

        // The identical-path bind mount is what makes the executor's absolute workspace
        // paths (in toolArgs) resolve to the same files inside the container.
        args.Should().ContainInOrder("-v", $"{workspace}:{workspace}");
        args.Should().ContainInOrder("-w", workspace);

        // Entrypoint override → the raw GDAL CLI on the image PATH, not the worker dll.
        args.Should().ContainInOrder("--entrypoint", "gdaldem");

        // Image precedes the tool args; tool args follow verbatim in order.
        var imageIndex = args.ToList().IndexOf(GdalContainerExecutionOptions.DefaultImage);
        imageIndex.Should().BeGreaterThan(0);
        args.Skip(imageIndex + 1).Should().Equal(toolArgs);

        // The image must come AFTER --entrypoint (docker run flag ordering) and the tool
        // args after the image, so the container runs `gdaldem <args>`.
        args.ToList().IndexOf("--entrypoint").Should().BeLessThan(imageIndex);
    }

    [UnitTest]
    public void BuildDockerRunArguments_HonorsCustomImageNetworkAndUser()
    {
        var options = new GdalContainerExecutionOptions
        {
            Image = "ghcr.io/honua-io/honua-worker-etl@sha256:abc",
            Network = "bridge",
            User = "0:0",
        };

        var args = DockerGdalCommandRunner.BuildDockerRunArguments(
            options, "ogr2ogr", new[] { "-f", "CSV", "/w/out.csv", "/w/in.geojson" }, "/w");

        args.Should().Contain("ghcr.io/honua-io/honua-worker-etl@sha256:abc");
        args.Should().ContainInOrder("--network", "bridge");
        args.Should().ContainInOrder("--user", "0:0");
    }

    [UnitTest]
    public async Task RunAsync_DelegatesTheBuiltArgvToTheContainerInvoker()
    {
        var invoker = new FakeDockerCommandInvoker(_ => new GdalCommandResult { ExitCode = 0 });
        var runner = new DockerGdalCommandRunner(
            invoker,
            Options.Create(new GdalContainerExecutionOptions()),
            Options.Create(new GdalHardeningOptions()),
            NullLogger<DockerGdalCommandRunner>.Instance);

        var result = await runner.RunAsync(
            "gdalwarp",
            new[] { "-t_srs", "EPSG:3857", "/scratch/op/in.tif", "/scratch/op/out.tif" },
            "/scratch/op",
            CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        invoker.Invocations.Should().ContainSingle();

        var invocation = invoker.Invocations[0];
        invocation.Executable.Should().Be("docker");
        invocation.Arguments.Should().ContainInOrder("run", "--rm");
        invocation.Arguments.Should().ContainInOrder("-v", "/scratch/op:/scratch/op");
        invocation.Arguments.Should().ContainInOrder("--entrypoint", "gdalwarp");
        invocation.Arguments.Should().ContainInOrder(GdalContainerExecutionOptions.DefaultImage, "-t_srs", "EPSG:3857");
    }

    private sealed class FakeDockerCommandInvoker(Func<IReadOnlyList<string>, GdalCommandResult> behavior)
        : IDockerCommandInvoker
    {
        public List<(string Executable, IReadOnlyList<string> Arguments)> Invocations { get; } = [];

        public Task<GdalCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            Invocations.Add((executable, arguments));
            return Task.FromResult(behavior(arguments));
        }

        public Task<bool> ImageExistsAsync(string executable, string image, CancellationToken cancellationToken)
            => Task.FromResult(true);
    }
}
