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
/// Native-profile <see cref="IJobExecutor"/> for the catalog <c>raster.reproject</c>
/// process. Distinct from <see cref="GdalRasterReprojectJobExecutor"/>
/// (which handles the direct <c>gdal.gdalwarp</c> id with <c>targetSrs</c> text
/// CRS tokens): this executor consumes the catalog's <c>targetSrid</c> (positive
/// 32-bit SRID) + <c>resampling</c> enum and shells to the same <c>gdalwarp</c>
/// CLI. Kept separate so the two surface contracts (direct gdal.* vs the
/// catalog's raster.* idioms) cannot drift from each other's argument parsing.
/// </summary>
public sealed partial class GdalRasterReprojectCatalogJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterReprojectCatalogJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.reproject";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Maps the catalog's canonical resampling enum onto gdalwarp's -r flag value.
    // Mirrors ProcessPlanValidator.RasterResamplingValues so executor + validator
    // agree on the accepted set. Unknown values are rejected at the executor
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
        await context.ReportProgressAsync(5, "Parsing reproject inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "targetSrid", out var targetSridRaw))
        {
            return JobExecutionResult.Failed("Invalid reproject inputs: missing required input 'targetSrid'.");
        }

        if (!int.TryParse(targetSridRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var targetSrid) || targetSrid <= 0)
        {
            return JobExecutionResult.Failed(
                $"Invalid reproject inputs: 'targetSrid' value '{targetSridRaw}' is not a positive integer.");
        }

        var resamplingFlag = "bilinear";
        if (GdalJobInputReader.TryGetInput(parameters, "resampling", out var resamplingRaw)
            && !string.IsNullOrWhiteSpace(resamplingRaw))
        {
            if (!ResamplingMap.TryGetValue(resamplingRaw.Trim(), out resamplingFlag))
            {
                return JobExecutionResult.Failed(
                    $"Invalid reproject inputs: 'resampling' value '{resamplingRaw}' is not in the allowed set " +
                    "(nearestneighbor, bilinear, cubic, lanczos).");
            }
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid reproject inputs: {sourceError}");
        }

        if (sourceBytes.Length > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed(
                $"Source raster {sourceBytes.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalwarp reproject", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-overwrite",
                "-of", "GTiff",
                "-t_srs", $"EPSG:{targetSrid.ToString(CultureInfo.InvariantCulture)}",
                "-r", resamplingFlag,
                inputPath,
                outputPath,
            };

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
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, Truncate(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalwarp exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdalwarp reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding reprojected raster artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdalwarp produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Reprojected raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Reproject completed", cancellationToken).ConfigureAwait(false);

            Log.ReprojectCompleted(logger, job.OperationId, targetSrid, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static partial class Log
    {
        [LoggerMessage(9270, LogLevel.Warning,
            "GDAL raster reproject (catalog) executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9271, LogLevel.Warning,
            "GDAL raster reproject (catalog) executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9272, LogLevel.Error,
            "GDAL raster reproject (catalog) executor failed job {OperationId}: gdalwarp exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9273, LogLevel.Error,
            "GDAL raster reproject (catalog) executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9274, LogLevel.Warning,
            "GDAL raster reproject (catalog) executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9275, LogLevel.Information,
            "GDAL raster reproject (catalog) executor completed job {OperationId}: targetSrid={TargetSrid}, bytes={Bytes}")]
        public static partial void ReprojectCompleted(ILogger logger, string operationId, int targetSrid, long bytes);
    }
}
