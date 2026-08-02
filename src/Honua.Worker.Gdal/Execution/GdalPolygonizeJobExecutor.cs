// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>conversion.polygonize</c>
/// process: vectorizes a raster into polygon features (one per connected region of
/// equal pixel value), backed by the GDAL <c>gdal_polygonize.py</c> CLI (#2240).
///
/// <para>
/// Reads a base64-encoded GeoTIFF from <c>source</c> and publishes a GeoJSON
/// FeatureCollection of polygons carrying the pixel value in the <c>fieldName</c>
/// attribute (default <c>DN</c>), using the requested band and connectedness.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalPolygonizeJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalPolygonizeJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "conversion.polygonize";

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
        await context.ReportProgressAsync(5, "Parsing polygonize inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        var band = 1;
        if (GdalJobInputReader.TryGetInput(parameters, "band", out var bandRaw) && !string.IsNullOrWhiteSpace(bandRaw)
            && (!int.TryParse(bandRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out band) || band < 1))
        {
            return JobExecutionResult.Failed(
                $"Invalid polygonize inputs: 'band' must be a positive integer; got '{bandRaw}'.");
        }

        var eightConnected = false;
        if (GdalJobInputReader.TryGetInput(parameters, "connectedness", out var connRaw) && !string.IsNullOrWhiteSpace(connRaw))
        {
            var conn = connRaw.Trim();
            if (string.Equals(conn, "8", StringComparison.Ordinal))
            {
                eightConnected = true;
            }
            else if (!string.Equals(conn, "4", StringComparison.Ordinal))
            {
                return JobExecutionResult.Failed(
                    $"Invalid polygonize inputs: 'connectedness' must be 4 or 8; got '{connRaw}'.");
            }
        }

        var fieldName = "DN";
        if (GdalJobInputReader.TryGetInput(parameters, "fieldName", out var fieldRaw) && !string.IsNullOrWhiteSpace(fieldRaw))
        {
            if (!GdalFieldName.IsValid(fieldRaw))
            {
                return JobExecutionResult.Failed(
                    $"Invalid polygonize inputs: 'fieldName' must match ^[A-Za-z_][A-Za-z0-9_]*$; got '{fieldRaw}'.");
            }
            fieldName = fieldRaw.Trim();
        }

        if (!GdalJobInputReader.TryGetRasterInput(parameters, "source", opts.MaxArtifactBytes, out var sourceInput, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid polygonize inputs: {sourceError}");
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
            if (sourceInput.InlineBytes is { } sourceBytes
                && !GdalRasterDimensionGuard.TryAdmit(sourceBytes, opts, out var dimensionError))
            {
                return JobExecutionResult.Failed($"Invalid raster input: {dimensionError}");
            }

            if (sourceInput.InlineBytes is { } inlineBytes)
            {
                await File.WriteAllBytesAsync(inputPath, inlineBytes, cancellationToken).ConfigureAwait(false);
            }

            // gdal_polygonize.py [-8] -b band -f fmt -q raster out_file layer fieldname.
            var args = new List<string>();
            if (eightConnected)
            {
                args.Add("-8");
            }
            args.Add("-b");
            args.Add(band.ToString(CultureInfo.InvariantCulture));
            args.Add("-f");
            args.Add("GeoJSON");
            args.Add("-q");
            sourceInput.AddReadPin(args);
            args.Add(inputPath);
            args.Add(outputPath);
            args.Add("polygonized");
            args.Add(fieldName);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_polygonize", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_polygonize.py", args, workspace, outputPath,
                GeoJsonContentType, "Polygonized vector",
                "Encoding polygonized vector artifact", "Polygonize completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9400, LogLevel.Warning,
            "GDAL polygonize executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9401, LogLevel.Warning,
            "GDAL polygonize executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
