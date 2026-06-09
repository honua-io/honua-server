// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Production <see cref="IGdalCommandRunner"/> that invokes the GDAL/OGR CLI
/// tools as child processes. The tools are provided by the GDAL base image the
/// worker container is built from; this runner does not bundle managed GDAL
/// bindings so it adds no native package dependencies to any managed assembly.
/// </summary>
internal sealed partial class ProcessGdalCommandRunner(ILogger<ProcessGdalCommandRunner> logger) : IGdalCommandRunner
{
    /// <inheritdoc />
    public async Task<GdalCommandResult> RunAsync(
        string tool,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = tool,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Log.RunningTool(logger, tool, string.Join(' ', arguments));

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
            throw new InvalidOperationException($"Failed to start GDAL tool '{tool}'.");
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

        var result = new GdalCommandResult
        {
            ExitCode = process.ExitCode,
            StandardOutput = stdout.ToString(),
            StandardError = stderr.ToString()
        };

        Log.ToolCompleted(logger, tool, result.ExitCode);
        return result;
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
        [LoggerMessage(9200, LogLevel.Information, "Running GDAL tool {Tool} {Arguments}")]
        public static partial void RunningTool(ILogger logger, string tool, string arguments);

        [LoggerMessage(9201, LogLevel.Information, "GDAL tool {Tool} exited with code {ExitCode}")]
        public static partial void ToolCompleted(ILogger logger, string tool, int exitCode);
    }
}
