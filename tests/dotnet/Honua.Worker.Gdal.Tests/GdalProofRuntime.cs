// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Tests;

/// <summary>Shared native transport for executor and public API proofs.</summary>
internal static class GdalProofRuntime
{
    internal static DockerGdalCommandRunner CreateRunner() => new(
        new PortableDockerInvoker(),
        Options.Create(new GdalContainerExecutionOptions { Image = ReadProductionImage(), User = Environment.GetEnvironmentVariable("HONUA_GDAL_PROOF_USER") ?? "1001:1001" }),
        Options.Create(new GdalHardeningOptions()), Options.Create(new AwsS3Options()),
        Options.Create(new AzureBlobOptions()), NullLogger<DockerGdalCommandRunner>.Instance);

    private static string ReadProductionImage()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Honua.sln")))
        {
            root = root.Parent;
        }
        root.Should().NotBeNull("the proof must consume the production worker's native dependency pin");
        const string prefix = "ARG GDAL_BASE_IMAGE=";
        var image = File.ReadLines(Path.Join(root!.FullName, "docker", "worker-gdal", "Dockerfile"))
            .Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        image.Should().Contain("@sha256:", "native correctness evidence must use an immutable tool image");
        return image;
    }

    // Only translate host paths at the Docker transport boundary. The production
    // runner still builds the command, applies hardening and invokes real tools.
    // This permits Windows dotnet + Docker Desktop without a Linux build host.
    private sealed class PortableDockerInvoker : IDockerCommandInvoker
    {
        private readonly ProcessDockerCommandInvoker _inner = new(NullLogger<ProcessDockerCommandInvoker>.Instance);

        public Task<GdalCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            IReadOnlyDictionary<string, string>? environment, CancellationToken cancellationToken)
        {
            var args = arguments.ToArray();
            var mountIndex = Array.IndexOf(args, "-v") + 1;
            var workIndex = Array.IndexOf(args, "-w") + 1;
            var workspace = args[workIndex];
            for (var i = workIndex; i < args.Length; i++)
            {
                args[i] = args[i].Replace(workspace, "/proof", StringComparison.Ordinal).Replace('\\', '/');
            }
            args[mountIndex] = workspace + ":/proof";
            return _inner.RunAsync(executable, args, environment, cancellationToken);
        }

        public Task<bool> ImageExistsAsync(string executable, string image, CancellationToken cancellationToken)
            => _inner.ImageExistsAsync(executable, image, cancellationToken);
    }
}
