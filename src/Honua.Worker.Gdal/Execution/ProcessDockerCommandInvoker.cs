// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Production <see cref="IDockerCommandInvoker"/> that launches the container
/// runtime (<c>docker</c> / <c>podman</c>) as a child process. Mirrors the child-
/// process management of <see cref="ProcessGdalCommandRunner"/> (no shell, captured
/// stdout/stderr, kill-on-cancel). It is exercised end-to-end only where a Docker
/// daemon is present (CI / local-Docker dev box); the argv it receives is the part
/// verified offline by <see cref="DockerGdalCommandRunner"/>'s unit tests.
/// </summary>
internal sealed partial class ProcessDockerCommandInvoker(ILogger<ProcessDockerCommandInvoker> logger)
    : IDockerCommandInvoker
{
    /// <inheritdoc />
    public async Task<GdalCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        Log.RunningContainer(logger, executable, string.Join(' ', arguments));

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            executable, arguments, throwIfStartFails: true, cancellationToken).ConfigureAwait(false);

        Log.ContainerCompleted(logger, executable, exitCode);
        return new GdalCommandResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
        };
    }

    /// <inheritdoc />
    public async Task<bool> ImageExistsAsync(
        string executable,
        string image,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        // `docker image inspect <ref>` exits 0 when the image is present locally and
        // non-zero (with no daemon access, or image absent) otherwise. Any launch
        // failure (no Docker installed) is treated as "not available" so auto-mode
        // silently falls back to the in-process path instead of throwing.
        try
        {
            var (exitCode, _, _) = await RunProcessAsync(
                executable,
                new[] { "image", "inspect", image },
                throwIfStartFails: false,
                cancellationToken).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ImageProbeFailed(logger, image, ex.Message);
            return false;
        }
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        bool throwIfStartFails,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        if (!process.Start())
        {
            if (throwIfStartFails)
            {
                throw new InvalidOperationException($"Failed to start container runtime '{executable}'.");
            }

            return (-1, "", "");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        // Ensure async stream readers flush before the buffers are read.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        return (process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Permission/race killing the process; nothing more we can do.
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Information, "Running container {Executable} {Arguments}")]
        public static partial void RunningContainer(ILogger logger, string executable, string arguments);

        [LoggerMessage(9271, LogLevel.Information, "Container {Executable} exited with code {ExitCode}")]
        public static partial void ContainerCompleted(ILogger logger, string executable, int exitCode);

        [LoggerMessage(9272, LogLevel.Debug, "Container image probe for {Image} failed: {Reason}")]
        public static partial void ImageProbeFailed(ILogger logger, string image, string reason);
    }
}
