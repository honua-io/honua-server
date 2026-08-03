// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>surface.contour</c>
/// process: generates contour LINES from a DEM, backed by the GDAL
/// <c>gdal_contour</c> CLI (#2240).
///
/// <para>
/// Reads a base64-encoded GeoTIFF DEM from <c>source</c> and emits a GeoJSON
/// FeatureCollection of contour lines carrying an <c>ELEV</c> elevation attribute,
/// at the requested <c>interval</c> and optional elevation <c>base</c> offset.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalContourJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalContourJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "surface.contour";

    private const string GeoJsonContentType = "application/geo+json";

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
        await context.ReportProgressAsync(5, "Parsing contour inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "interval", out var intervalRaw) || string.IsNullOrWhiteSpace(intervalRaw))
        {
            return JobExecutionResult.Failed("Invalid contour inputs: missing required input 'interval'.");
        }

        if (!double.TryParse(intervalRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var interval)
            || double.IsNaN(interval) || double.IsInfinity(interval) || interval <= 0d)
        {
            return JobExecutionResult.Failed(
                $"Invalid contour inputs: 'interval' must be a positive finite number; got '{intervalRaw}'.");
        }

        double? offset = null;
        if (GdalJobInputReader.TryGetInput(parameters, "base", out var baseRaw) && !string.IsNullOrWhiteSpace(baseRaw))
        {
            if (!double.TryParse(baseRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                return JobExecutionResult.Failed(
                    $"Invalid contour inputs: 'base' must be a finite number; got '{baseRaw}'.");
            }
            offset = parsed;
        }

        if (!GdalJobInputReader.TryGetRasterInput(parameters, "source", opts.MaxArtifactBytes, out var sourceInput, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid contour inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Both second segments are fixed relative literal filenames, so they can
            // never be rooted and silently discard workspace.
            var inputPath = sourceInput.ReferencedPath ?? Path.Join(workspace, "input.tif");
            var outputPath = Path.Join(workspace, "output.geojson");
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

            var args = new List<string>
            {
                "-a", "ELEV",
                "-i", interval.ToString("R", CultureInfo.InvariantCulture),
                "-f", "GeoJSON",
            };
            if (offset.HasValue)
            {
                args.Add("-off");
                args.Add(offset.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            sourceInput.AddReadPin(args);
            args.Add(inputPath);
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_contour", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_contour", args, workspace, outputPath,
                GeoJsonContentType, "Contour vector",
                "Encoding contour vector artifact", "Contour completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9380, LogLevel.Warning,
            "GDAL contour executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9381, LogLevel.Warning,
            "GDAL contour executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
