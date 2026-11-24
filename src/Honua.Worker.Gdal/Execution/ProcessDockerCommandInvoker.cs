// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using Honua.Core.Features.Infrastructure.Subprocesses;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

internal sealed partial class ProcessDockerCommandInvoker(ILogger<ProcessDockerCommandInvoker> logger)
    : IDockerCommandInvoker
{
    public async Task<GdalCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentNullException.ThrowIfNull(arguments);

        var (effectiveArguments, containerName) = EnsureRunContainerName(arguments);
        Log.RunningContainer(logger, executable, string.Join(' ', effectiveArguments));

        var (exitCode, stdout, stderr, stdoutTruncated) = await RunProcessAsync(
            executable, effectiveArguments, environment, containerName, throwIfStartFails: true, cancellationToken).ConfigureAwait(false);

        Log.ContainerCompleted(logger, executable, exitCode);
        return new GdalCommandResult
        {
            ExitCode = exitCode,
            StandardOutput = stdout,
            StandardError = stderr,
            StandardOutputTruncated = stdoutTruncated,
        };
    }

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
            var (exitCode, _, _, _) = await RunProcessAsync(
                executable,
                new[] { "image", "inspect", image },
                environment: null,
                containerName: null,
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

    internal static (IReadOnlyList<string> Arguments, string? ContainerName) EnsureRunContainerName(
        IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0 || !string.Equals(arguments[0], "run", StringComparison.Ordinal))
        {
            return (arguments, null);
        }

        for (var i = 1; i < arguments.Count; i++)
        {
            if (string.Equals(arguments[i], "--name", StringComparison.Ordinal) && i + 1 < arguments.Count)
            {
                return (arguments, arguments[i + 1]);
            }

            if (arguments[i].StartsWith("--name=", StringComparison.Ordinal))
            {
                return (arguments, arguments[i]["--name=".Length..]);
            }
        }

        var containerName = $"honua-gdal-{Guid.NewGuid():N}";
        var updated = new List<string>(arguments.Count + 2) { arguments[0], "--name", containerName };
        for (var i = 1; i < arguments.Count; i++)
        {
            updated.Add(arguments[i]);
        }

        return (updated, containerName);
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError, bool StandardOutputTruncated)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment,
        string? containerName,
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

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stdout = new BoundedProcessOutputBuffer(
            BoundedProcessOutputBuffer.DefaultStandardOutputMaxChars,
            appendTruncationMarker: false);
        var stderr = new BoundedProcessOutputBuffer();

        process.OutputDataReceived += (_, e) => stdout.AppendLine(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.AppendLine(e.Data);

        if (!process.Start())
        {
            if (throwIfStartFails)
            {
                throw new InvalidOperationException($"Failed to start container runtime '{executable}'.");
            }

            return (-1, "", "", false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            SubprocessEnvironmentScrubber.TryKill(process);
            if (!string.IsNullOrWhiteSpace(containerName))
            {
                await TryForceRemoveContainerAsync(executable, containerName).ConfigureAwait(false);
            }
            throw;
        }

        // Ensure async stream readers flush before the buffers are read.
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);

        return (process.ExitCode, stdout.ToString(), stderr.ToString(), stdout.Truncated);
    }

    private static async Task TryForceRemoveContainerAsync(string executable, string containerName)
    {
        try
        {
            using var rmProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };
            rmProcess.StartInfo.ArgumentList.Add("rm");
            rmProcess.StartInfo.ArgumentList.Add("-f");
            rmProcess.StartInfo.ArgumentList.Add(containerName);
            rmProcess.OutputDataReceived += static (_, _) => { };
            rmProcess.ErrorDataReceived += static (_, _) => { };
            if (!rmProcess.Start())
            {
                return;
            }

            rmProcess.BeginOutputReadLine();
            rmProcess.BeginErrorReadLine();

            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await rmProcess.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                SubprocessEnvironmentScrubber.TryKill(rmProcess);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup; the container may have already exited with --rm.
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
