// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Honua.ControlPlane;

/// <summary>Result of one allowlisted docker invocation. Non-zero exits are returned, not thrown.</summary>
internal readonly record struct DockerProcessResult(int ExitCode, string Stdout, string Stderr);

/// <summary>Process seam so unit tests can prove a refusal never starts docker.</summary>
internal interface IDockerComposeProcess
{
    /// <summary>Runs <c>docker</c> with the supplied argument list and no shell.</summary>
    Task<DockerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts <c>docker</c> with <see cref="ProcessStartInfo.ArgumentList"/> only. There is no shell,
/// no extra environment, and no caller-supplied executable path.
/// </summary>
internal sealed class DockerComposeProcess : IDockerComposeProcess
{
    private const int MaxCapturedCharacters = 256_000;

    /// <inheritdoc />
    public async Task<DockerProcessResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var start = new ProcessStartInfo
        {
            FileName = "docker",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return new DockerProcessResult(127, string.Empty, "docker did not start");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            return new DockerProcessResult(127, string.Empty, "docker was not found");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var stdout = ReadCappedAsync(process.StandardOutput, timeoutSource.Token);
        var stderr = ReadCappedAsync(process.StandardError, timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            return new DockerProcessResult(124, await DrainAsync(stdout).ConfigureAwait(false), "timed out");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }

        return new DockerProcessResult(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }

    private static async Task<string> DrainAsync(Task<string> read)
    {
        try
        {
            return await read.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder();
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (builder.Length < MaxCapturedCharacters)
            {
                builder.Append(buffer, 0, Math.Min(read, MaxCapturedCharacters - builder.Length));
            }
        }

        return builder.ToString();
    }

    private static void Kill(Process process)
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
            // The process already exited between the check and the kill.
        }
    }
}
