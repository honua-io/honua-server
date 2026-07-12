// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>surface.viewshed</c>
/// process: computes a binary visibility raster from a DEM and an observer location,
/// backed by the GDAL <c>gdal_viewshed</c> CLI (#2240).
///
/// <para>
/// Reads a base64-encoded GeoTIFF DEM from <c>source</c>, places the observer at
/// (<c>observerX</c>, <c>observerY</c>) in the DEM's georeferenced units at
/// <c>observerHeight</c> above the surface, and publishes the visibility GeoTIFF,
/// optionally bounded by <c>maxDistance</c> and using a <c>targetHeight</c> offset.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalViewshedJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalViewshedJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "surface.viewshed";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

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
        await context.ReportProgressAsync(5, "Parsing viewshed inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!TryReadFinite(parameters, "observerX", out var observerX, out var oxError))
        {
            return JobExecutionResult.Failed($"Invalid viewshed inputs: {oxError}");
        }

        if (!TryReadFinite(parameters, "observerY", out var observerY, out var oyError))
        {
            return JobExecutionResult.Failed($"Invalid viewshed inputs: {oyError}");
        }

        if (!TryReadOptionalNonNegative(parameters, "observerHeight", 2.0, out var observerHeight, out var ohError))
        {
            return JobExecutionResult.Failed($"Invalid viewshed inputs: {ohError}");
        }

        if (!TryReadOptionalNonNegative(parameters, "targetHeight", 0.0, out var targetHeight, out var thError))
        {
            return JobExecutionResult.Failed($"Invalid viewshed inputs: {thError}");
        }

        double? maxDistance = null;
        if (GdalJobInputReader.TryGetInput(parameters, "maxDistance", out var maxRaw) && !string.IsNullOrWhiteSpace(maxRaw))
        {
            if (!double.TryParse(maxRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0d)
            {
                return JobExecutionResult.Failed(
                    $"Invalid viewshed inputs: 'maxDistance' must be a positive finite number; got '{maxRaw}'.");
            }
            maxDistance = parsed;
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid viewshed inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            // Bound the DECLARED pixel footprint before invoking GDAL so a
            // compressible GeoTIFF declaring enormous dimensions cannot force a
            // decompression-bomb allocation (#2766).
            if (!GdalRasterDimensionGuard.TryAdmit(sourceBytes, opts, out var dimensionError))
            {
                return JobExecutionResult.Failed($"Invalid raster input: {dimensionError}");
            }

            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-of", "GTiff",
                "-ox", observerX.ToString("R", CultureInfo.InvariantCulture),
                "-oy", observerY.ToString("R", CultureInfo.InvariantCulture),
                "-oz", observerHeight.ToString("R", CultureInfo.InvariantCulture),
                "-tz", targetHeight.ToString("R", CultureInfo.InvariantCulture),
            };
            if (maxDistance.HasValue)
            {
                args.Add("-md");
                args.Add(maxDistance.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            args.Add(inputPath);
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_viewshed", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_viewshed", args, workspace, outputPath,
                GeoTiffContentType, "Viewshed raster",
                "Encoding viewshed raster artifact", "Viewshed completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static bool TryReadFinite(
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

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value) || double.IsInfinity(value))
        {
            failure = $"{name} must be a finite number; got '{raw}'";
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalNonNegative(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue,
        out double value,
        out string failure)
    {
        value = defaultValue;
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            || double.IsNaN(value) || double.IsInfinity(value) || value < 0d)
        {
            failure = $"{name} must be a non-negative finite number; got '{raw}'";
            value = defaultValue;
            return false;
        }

        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9390, LogLevel.Warning,
            "GDAL viewshed executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9391, LogLevel.Warning,
            "GDAL viewshed executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
