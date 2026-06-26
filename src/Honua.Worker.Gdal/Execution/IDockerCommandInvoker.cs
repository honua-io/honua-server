// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Seam over launching the container runtime (<c>docker</c> / <c>podman</c>) as a
/// child process. <see cref="DockerGdalCommandRunner"/> builds the full
/// <c>docker run …</c> argument vector (image, mounts, user, network, entrypoint
/// tool, tool args) and hands it to this invoker, which actually starts the
/// process. Splitting the argv-construction from the process launch lets the
/// argv — the load-bearing, correct-by-construction part (image ref, mount path,
/// uid, entrypoint) — be asserted by an offline unit test with a fake invoker on a
/// host that has no Docker daemon, while the real <see cref="ProcessDockerCommandInvoker"/>
/// runs the container in CI / on a local Docker box.
/// </summary>
internal interface IDockerCommandInvoker
{
    /// <summary>
    /// Runs the container runtime executable with the supplied argument vector.
    /// </summary>
    /// <param name="executable">The container runtime, e.g. <c>docker</c>.</param>
    /// <param name="arguments">
    /// The full argument vector after the executable — typically
    /// <c>["run", "--rm", …, image, tool, …toolArgs]</c>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token; kills the process when triggered.</param>
    Task<GdalCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Probes whether the named container image is present locally (so the GP Devkit
    /// runner can auto-enable container mode only when the image is already pulled,
    /// rather than blocking the sub-second loop on a pull). Returns <c>false</c> when
    /// the container runtime is unavailable or the image is absent.
    /// </summary>
    /// <param name="executable">The container runtime, e.g. <c>docker</c>.</param>
    /// <param name="image">The image reference to probe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<bool> ImageExistsAsync(
        string executable,
        string image,
        CancellationToken cancellationToken);
}
