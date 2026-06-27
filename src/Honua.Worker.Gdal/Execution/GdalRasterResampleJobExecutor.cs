// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>raster.resample</c>
/// process: changes a raster's cell size using the requested resampling
/// algorithm, backed by the GDAL <c>gdalwarp -tr</c> CLI.
///
/// Reads a base64-encoded GeoTIFF from the canonical <c>source</c> input and a
/// target cell size from <c>cellSize</c> (optionally a distinct vertical size via
/// <c>cellSizeY</c>), warps in an isolated scratch workspace, and publishes the
/// resampled GeoTIFF as a canonical data-URI artifact. Runs only inside the GDAL
/// worker image — <see cref="AcceptedRuntimeProfiles"/> is <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterResampleJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterResampleJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.resample";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Maps the catalog's canonical resampling enum onto gdalwarp's -r flag value.
    // Mirrors ProcessPlanValidator.RasterResamplingValues so executor + validator
    // agree on the accepted set; unknown values are rejected at the executor
    // boundary to defend against bypassed validation.
    private static readonly FrozenDictionary<string, string> ResamplingMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nearestneighbor"] = "near",
            ["nearest-neighbor"] = "near",
            ["nearest"] = "near",
            ["bilinear"] = "bilinear",
            ["cubic"] = "cubic",
            ["bicubic"] = "cubic",
            ["lanczos"] = "lanczos",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds { get; } =
        new HashSet<string>(StringComparer.Ordinal) { HandledProcessId };

    /// <inheritdoc />
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
        await context.ReportProgressAsync(5, "Parsing resample inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!TryReadPositiveDouble(parameters, "cellSize", out var cellSizeX, out var cellError))
        {
            return JobExecutionResult.Failed($"Invalid resample inputs: {cellError}");
        }

        var cellSizeY = cellSizeX;
        if (GdalJobInputReader.TryGetInput(parameters, "cellSizeY", out var cellSizeYRaw)
            && !string.IsNullOrWhiteSpace(cellSizeYRaw))
        {
            if (!TryReadPositiveDouble(parameters, "cellSizeY", out cellSizeY, out var cellYError))
            {
                return JobExecutionResult.Failed($"Invalid resample inputs: {cellYError}");
            }
        }

        var resamplingFlag = "bilinear";
        if (GdalJobInputReader.TryGetInput(parameters, "resampling", out var resamplingRaw)
            && !string.IsNullOrWhiteSpace(resamplingRaw))
        {
            if (!ResamplingMap.TryGetValue(resamplingRaw.Trim(), out resamplingFlag))
            {
                return JobExecutionResult.Failed(
                    $"Invalid resample inputs: 'resampling' value '{resamplingRaw}' is not in the allowed set " +
                    "(nearestneighbor, bilinear, cubic, lanczos).");
            }
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid resample inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalwarp resample", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-overwrite",
                "-of", "GTiff",
                "-tr", FormatDouble(cellSizeX), FormatDouble(cellSizeY),
                "-r", resamplingFlag,
                inputPath,
                outputPath,
            };

            await GdalCommandLog.LogCommandAsync(context, "gdalwarp", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdalwarp", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdalwarp timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalwarp exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdalwarp reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding resampled raster artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdalwarp produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Resampled raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Resample completed", cancellationToken).ConfigureAwait(false);

            Log.ResampleCompleted(logger, job.OperationId, cellSizeX, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static bool TryReadPositiveDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out double value,
        out string failure)
    {
        value = 0d;
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failure = $"missing required input '{name}'";
            return false;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed <= 0d)
        {
            failure = $"{name} must be a positive finite number; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatDouble(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static partial class Log
    {
        [LoggerMessage(9310, LogLevel.Warning,
            "GDAL raster resample executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9311, LogLevel.Warning,
            "GDAL raster resample executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9312, LogLevel.Error,
            "GDAL raster resample executor failed job {OperationId}: gdalwarp exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9313, LogLevel.Error,
            "GDAL raster resample executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9314, LogLevel.Warning,
            "GDAL raster resample executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9315, LogLevel.Information,
            "GDAL raster resample executor completed job {OperationId}: cellSize={CellSize}, bytes={Bytes}")]
        public static partial void ResampleCompleted(ILogger logger, string operationId, double cellSize, long bytes);
    }
}
