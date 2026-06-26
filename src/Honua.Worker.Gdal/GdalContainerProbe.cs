// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Worker.Gdal;

/// <summary>
/// Public probe the GP Devkit CLI uses to decide whether the container-exec
/// fidelity path (<see cref="GdalProcessExecutorMode.Container"/>, issue #2180) can
/// run on this host — i.e. whether the worker image is already present locally —
/// without forcing the CLI to reach into the worker assembly's internal
/// container-runtime seam.
/// </summary>
public static class GdalContainerProbe
{
    /// <summary>
    /// The default container image reference the probe checks for and the
    /// container-exec runner runs. Matches the local build tag produced by
    /// <c>docker/worker-gdal/Dockerfile</c>.
    /// </summary>
    public static string DefaultImage => GdalContainerExecutionOptions.DefaultImage;

    /// <summary>
    /// Returns whether the named worker image is present in the local container
    /// runtime. Returns <c>false</c> (never throws) when no container runtime is
    /// installed or the image has not been pulled/built — so the CLI's auto-mode can
    /// fall back to the fast in-process path silently.
    /// </summary>
    /// <param name="image">Image reference; defaults to <see cref="DefaultImage"/>.</param>
    /// <param name="dockerExecutable">Container runtime; defaults to <c>docker</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public static async Task<bool> IsImageAvailableAsync(
        string? image = null,
        string dockerExecutable = "docker",
        CancellationToken cancellationToken = default)
    {
        var invoker = new ProcessDockerCommandInvoker(NullLogger<ProcessDockerCommandInvoker>.Instance);
        return await invoker.ImageExistsAsync(
            dockerExecutable,
            string.IsNullOrWhiteSpace(image) ? GdalContainerExecutionOptions.DefaultImage : image,
            cancellationToken).ConfigureAwait(false);
    }
}
