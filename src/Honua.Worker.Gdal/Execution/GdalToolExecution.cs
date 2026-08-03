// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Shared "run a GDAL/OGR CLI tool and publish its single file output" tail used
/// by the raster-analysis / terrain-derivative / raster&lt;-&gt;vector executors
/// (#2239, #2240). Centralizes the identical timeout fence, exit-code handling,
/// output-existence / empty / size guards, and canonical data-URI artifact
/// publication so every executor enforces the same cross-cutting behavior (error
/// sanitization, MaxArtifactBytes ceiling, structured run logging) rather than
/// each copy-pasting the boilerplate.
///
/// <para>
/// Each executor remains responsible for the genuinely per-tool work — parsing /
/// guarding its inputs and projecting them onto the CLI argument list — and then
/// hands the assembled command here for execution.
/// </para>
/// </summary>
internal static partial class GdalToolExecution
{
    /// <summary>
    /// Runs <paramref name="tool"/> with the supplied arguments inside the scratch
    /// <paramref name="workspace"/>, then publishes <paramref name="outputPath"/> as
    /// a canonical <c>data:&lt;contentType&gt;;base64,...</c> artifact. The command
    /// is logged (scratch-redacted) before execution and bounded by the worker's
    /// <see cref="GdalWorkerOptions.ToolTimeout"/> and
    /// <see cref="GdalWorkerOptions.MaxArtifactBytes"/>.
    /// </summary>
    public static async Task<JobExecutionResult> RunAndPublishAsync(
        IGdalCommandRunner runner,
        IJobExecutionContext context,
        GdalWorkerOptions options,
        ILogger logger,
        string operationId,
        string tool,
        IReadOnlyList<string> args,
        string workspace,
        string outputPath,
        string contentType,
        string artifactLabel,
        string encodingPhase,
        string completedPhase,
        CancellationToken cancellationToken)
    {
        await GdalCommandLog.LogCommandAsync(context, tool, args, workspace, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = new CancellationTokenSource(options.ToolTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        GdalCommandResult result;
        try
        {
            result = await runner.RunAsync(tool, args, workspace, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            Log.ToolTimedOut(logger, operationId, tool, options.ToolTimeout);
            return JobExecutionResult.Failed($"{tool} timed out after {options.ToolTimeout}.");
        }

        if (!result.Succeeded)
        {
            Log.ToolFailed(logger, operationId, tool, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
            return JobExecutionResult.Failed(
                $"{tool} exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
        }

        if (!File.Exists(outputPath))
        {
            return JobExecutionResult.Failed($"{tool} reported success but produced no output.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(80, encodingPhase, cancellationToken).ConfigureAwait(false);

        var publication = await GdalArtifactPublication.PublishFileAsync(
            context,
            outputPath,
            contentType,
            options.MaxArtifactBytes,
            artifactLabel,
            cancellationToken).ConfigureAwait(false);
        if (!publication.Succeeded)
        {
            if (publication.SizeBytes > options.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, operationId, tool, publication.SizeBytes, options.MaxArtifactBytes);
            }

            return JobExecutionResult.Failed(publication.ErrorMessage!);
        }

        await context.ReportProgressAsync(100, completedPhase, cancellationToken).ConfigureAwait(false);

        Log.ToolCompleted(logger, operationId, tool, publication.SizeBytes);
        return JobExecutionResult.Succeeded();
    }

    private static partial class Log
    {
        [LoggerMessage(9290, LogLevel.Error,
            "GDAL tool {Tool} failed job {OperationId}: exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, string tool, int exitCode, string error);

        [LoggerMessage(9291, LogLevel.Error,
            "GDAL tool {Tool} timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, string tool, TimeSpan timeout);

        [LoggerMessage(9292, LogLevel.Warning,
            "GDAL tool {Tool} refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, string tool, long actualBytes, long maxBytes);

        [LoggerMessage(9293, LogLevel.Information,
            "GDAL tool {Tool} completed job {OperationId}: bytes={Bytes}")]
        public static partial void ToolCompleted(ILogger logger, string operationId, string tool, long bytes);
    }
}
