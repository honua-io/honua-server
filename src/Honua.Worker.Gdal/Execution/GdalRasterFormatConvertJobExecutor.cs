// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the catalog
/// <c>conversion.raster-format</c> process (#2138). Exports a source raster into
/// another raster format (GTiff, PNG, JPEG, COG) via the real GDAL
/// <c>gdal_translate -of &lt;driver&gt;</c> CLI, replacing the previous
/// validation-only catalog stub. Reads a base64 GeoTIFF source, runs the
/// translation in an isolated scratch workspace, and publishes the converted
/// raster as a canonical data-URI artifact with the target format's content type.
/// GDAL execution failures surface as proper GP job failures with sanitized
/// messages rather than validation passes. Runs only inside the GDAL worker image
/// — <see cref="AcceptedRuntimeProfiles"/> is <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterFormatConvertJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterFormatConvertJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "conversion.raster-format";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Maps the catalog's targetFormat enum onto the gdal_translate -of driver name,
    // the scratch output extension, and the published artifact content type.
    // Mirrors ProcessPlanValidator.RasterFormatValues so executor + validator agree.
    private static readonly FrozenDictionary<string, FormatTarget> Formats =
        new Dictionary<string, FormatTarget>(StringComparer.OrdinalIgnoreCase)
        {
            ["GTiff"] = new FormatTarget("GTiff", "tif", "image/tiff; application=geotiff", SupportsCompression: true),
            ["COG"] = new FormatTarget("COG", "tif", "image/tiff; application=geotiff", SupportsCompression: true),
            ["PNG"] = new FormatTarget("PNG", "png", "image/png", SupportsCompression: false),
            ["JPEG"] = new FormatTarget("JPEG", "jpg", "image/jpeg", SupportsCompression: false),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

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

        if (!GdalJobInputReader.TryGetInput(parameters, "targetFormat", out var targetFormatRaw)
            || string.IsNullOrWhiteSpace(targetFormatRaw))
        {
            return JobExecutionResult.Failed("Invalid raster-format inputs: missing required input 'targetFormat'.");
        }

        if (!Formats.TryGetValue(targetFormatRaw.Trim(), out var format))
        {
            return JobExecutionResult.Failed(
                $"Invalid raster-format inputs: 'targetFormat' value '{targetFormatRaw}' is not in the allowed set " +
                "(GTiff, PNG, JPEG, COG).");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid raster-format inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Second segment of each call is a fixed relative literal, or built from
            // format.Extension which is only ever a fixed literal drawn from the
            // Formats allowlist above (never user-supplied), so neither can be rooted
            // and silently discard workspace.
            var inputPath = Path.Join(workspace, "input.tif");
            var outputPath = Path.Join(workspace, $"output.{format.Extension}");
            // Bound the DECLARED pixel footprint before invoking GDAL so a
            // compressible GeoTIFF declaring enormous dimensions cannot force a
            // decompression-bomb allocation (#2766).
            if (!GdalRasterDimensionGuard.TryAdmit(sourceBytes, opts, out var dimensionError))
            {
                return JobExecutionResult.Failed($"Invalid raster input: {dimensionError}");
            }

            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

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

            args.Add(inputPath);
            args.Add(outputPath);

            await GdalCommandLog.LogCommandAsync(context, "gdal_translate", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdal_translate", args, workspace, linked.Token).ConfigureAwait(false);
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

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdal_translate produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Converted raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(format.ContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Raster format conversion completed", cancellationToken).ConfigureAwait(false);

            Log.ConvertCompleted(logger, job.OperationId, format.Driver, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private readonly record struct FormatTarget(string Driver, string Extension, string ContentType, bool SupportsCompression);

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

        [LoggerMessage(9284, LogLevel.Warning,
            "GDAL raster format executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9285, LogLevel.Information,
            "GDAL raster format executor completed job {OperationId}: driver={Driver}, bytes={Bytes}")]
        public static partial void ConvertCompleted(ILogger logger, string operationId, string driver, long bytes);
    }
}
