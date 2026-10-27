// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the catalog
/// <c>conversion.raster-format</c> process (#2138). Exports a source raster into
/// another raster format (GTiff — also spelled GeoTIFF/TIFF/TIF — PNG, JPEG —
/// also spelled JPG — and COG) via the real GDAL
/// <c>gdal_translate -of &lt;driver&gt;</c> CLI, replacing the previous
/// validation-only catalog stub. Reads a base64 GeoTIFF source, runs the
/// translation in an isolated scratch workspace, and publishes the converted
/// raster as a canonical data-URI artifact with the target format's content type.
/// GDAL execution failures surface as proper GP job failures with sanitized
/// messages rather than validation passes. Runs only inside the GDAL worker image
/// — <see cref="AcceptedRuntimeProfiles"/> is <c>{ "native" }</c>.
/// A target driver disabled by <c>GdalWorker:Hardening:SkipDrivers</c> is rejected
/// at admission (#3307) rather than failing later inside GDAL.
/// </summary>
internal sealed partial class GdalRasterFormatConvertJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterFormatConvertJobExecutor> logger,
    IOptions<GdalHardeningOptions>? hardening = null) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "conversion.raster-format";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <inheritdoc />
    /// <summary>
    /// The single process id this executor handles, surfaced through
    /// <see cref="IProcessExecutor"/> so the GDAL dispatcher auto-registers it.
    /// </summary>
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    public ExecutionJobKind Kind => ExecutionJobKind.Geoprocessing;

    /// <inheritdoc />
    public IReadOnlySet<string> AcceptedRuntimeProfiles => NativeProfileSet;

    /// <inheritdoc />
    public async Task<JobExecutionResult> ExecuteAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);

        var parameters = job.Spec.Parameters;
        var processId = GdalJobInputReader.ResolveProcessId(parameters);
        if (!string.Equals(processId, HandledProcessId, StringComparison.Ordinal))
        {
            Log.UnsupportedProcessId(logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the {HandledProcessId} executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing raster-format inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "targetFormat", out var targetFormatRaw))
        {
            targetFormatRaw = string.Empty;
        }

        // Same fixed IOptions lifetime as ProcessGdalCommandRunner and
        // DockerGdalCommandRunner (hardening.Value). IOptionsMonitor.CurrentValue
        // would admit a reloaded SkipDrivers set the subprocess GDAL_SKIP does not use.
        var hardeningOptions = hardening?.Value ?? new GdalHardeningOptions();
        if (!GdalRasterOutputDriverAdmission.TryAdmit(
                targetFormatRaw, hardeningOptions, out var format, out var admissionError))
        {
            return JobExecutionResult.Failed(admissionError);
        }

        if (!GdalJobInputReader.TryGetRasterInput(parameters, "source", opts.MaxArtifactBytes, out var sourceInput, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid raster-format inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Second segment of each call is a fixed relative literal, or built from
            // format.Extension which is only ever a fixed literal drawn from the
            // GdalRasterOutputDriverAdmission allowlist (never user-supplied), so
            // neither can be rooted and silently discard workspace.
            var inputPath = sourceInput.ReferencedPath ?? Path.Join(workspace, "input.tif");
            var outputPath = Path.Join(workspace, $"output.{format.Extension}");
            // Bound the DECLARED pixel footprint before invoking GDAL so a
            // compressible GeoTIFF declaring enormous dimensions cannot force a
            // decompression-bomb allocation (#2766).
            if (!sourceInput.TryAdmit(opts, out var dimensionError))
            {
                return JobExecutionResult.Failed($"Invalid raster input: {dimensionError}");
            }

            if (sourceInput.InlineBytes is { } inlineBytes)
            {
                await File.WriteAllBytesAsync(inputPath, inlineBytes, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_translate", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-of", format.Driver,
            };

            if (format.SupportsCompression
                && GdalJobInputReader.TryGetInput(parameters, "compression", out var compression)
                && !string.IsNullOrWhiteSpace(compression))
            {
                args.Add("-co");
                args.Add($"COMPRESS={compression.Trim()}");
            }

            sourceInput.AddReadPin(args);
            args.Add(inputPath);
            args.Add(outputPath);

            await GdalCommandLog.LogCommandAsync(context, "gdal_translate", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await GdalRasterDriverAllowlist.RunAsync(runner, "gdal_translate", args, opts, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdal_translate timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdal_translate exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdal_translate reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding converted raster artifact", cancellationToken).ConfigureAwait(false);

            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength == 0)
            {
                return JobExecutionResult.Failed("gdal_translate produced an empty output raster.");
            }

            var publishError = await GdalArtifactPublisher.PublishFileAsync(
                context, opts, logger, job.OperationId, outputPath, format.ContentType,
                "Converted raster", cancellationToken).ConfigureAwait(false);
            if (publishError is not null)
            {
                return JobExecutionResult.Failed(publishError);
            }

            await context.ReportProgressAsync(100, "Raster format conversion completed", cancellationToken).ConfigureAwait(false);

            Log.ConvertCompleted(logger, job.OperationId, format.Driver, outputLength);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9280, LogLevel.Warning,
            "GDAL raster format executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9281, LogLevel.Warning,
            "GDAL raster format executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9282, LogLevel.Error,
            "GDAL raster format executor failed job {OperationId}: gdal_translate exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9283, LogLevel.Error,
            "GDAL raster format executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9285, LogLevel.Information,
            "GDAL raster format executor completed job {OperationId}: driver={Driver}, bytes={Bytes}")]
        public static partial void ConvertCompleted(ILogger logger, string operationId, string driver, long bytes);
    }
}
