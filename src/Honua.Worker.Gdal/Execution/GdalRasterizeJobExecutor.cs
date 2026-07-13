// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>conversion.rasterize</c>
/// process: burns vector features into a new raster grid, backed by the GDAL
/// <c>gdal_rasterize</c> CLI (#2240).
///
/// <para>
/// Reads a base64-encoded GeoJSON FeatureCollection from <c>source</c> and burns
/// either a fixed <c>burnValue</c> or a numeric <c>attribute</c> into a GeoTIFF whose
/// grid is defined by <c>cellSize</c> (resolution) or <c>width</c>+<c>height</c>
/// (pixel dimensions). The output extent is taken from the input layer.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterizeJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterizeJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "conversion.rasterize";

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
        await context.ReportProgressAsync(5, "Parsing rasterize inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!TryBuildBurnArgs(parameters, out var burnArgs, out var burnError))
        {
            return JobExecutionResult.Failed($"Invalid rasterize inputs: {burnError}");
        }

        if (!TryBuildGridArgs(parameters, opts, out var gridArgs, out var gridError))
        {
            return JobExecutionResult.Failed($"Invalid rasterize inputs: {gridError}");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid rasterize inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Combine(workspace, "input.geojson");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            var args = new List<string> { "-of", "GTiff" };
            args.AddRange(burnArgs);
            args.AddRange(gridArgs);

            if (GdalJobInputReader.TryGetInput(parameters, "nodata", out var nodataRaw) && !string.IsNullOrWhiteSpace(nodataRaw))
            {
                if (!double.TryParse(nodataRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var nodata)
                    || double.IsNaN(nodata) || double.IsInfinity(nodata))
                {
                    return JobExecutionResult.Failed(
                        $"Invalid rasterize inputs: 'nodata' must be a finite number; got '{nodataRaw}'.");
                }
                args.Add("-a_nodata");
                args.Add(nodata.ToString("R", CultureInfo.InvariantCulture));
            }

            args.Add(inputPath);
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_rasterize", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_rasterize", args, workspace, outputPath,
                GeoTiffContentType, "Rasterized raster",
                "Encoding rasterized raster artifact", "Rasterize completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Requires exactly one of <c>burnValue</c> (fixed value) or <c>attribute</c>
    /// (numeric source field) and projects it onto <c>-burn</c> / <c>-a</c>.
    /// </summary>
    private static bool TryBuildBurnArgs(
        IReadOnlyDictionary<string, string> parameters,
        out List<string> args,
        out string failure)
    {
        args = [];
        failure = "";

        var hasBurn = GdalJobInputReader.TryGetInput(parameters, "burnValue", out var burnRaw) && !string.IsNullOrWhiteSpace(burnRaw);
        var hasAttribute = GdalJobInputReader.TryGetInput(parameters, "attribute", out var attrRaw) && !string.IsNullOrWhiteSpace(attrRaw);

        if (hasBurn == hasAttribute)
        {
            failure = "supply exactly one of 'burnValue' or 'attribute'";
            return false;
        }

        if (hasBurn)
        {
            if (!double.TryParse(burnRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var burn)
                || double.IsNaN(burn) || double.IsInfinity(burn))
            {
                failure = $"'burnValue' must be a finite number; got '{burnRaw}'";
                return false;
            }
            args.Add("-burn");
            args.Add(burn.ToString("R", CultureInfo.InvariantCulture));
            return true;
        }

        if (!GdalFieldName.IsValid(attrRaw))
        {
            failure = $"'attribute' must match ^[A-Za-z_][A-Za-z0-9_]*$; got '{attrRaw}'";
            return false;
        }
        args.Add("-a");
        args.Add(attrRaw.Trim());
        return true;
    }

    /// <summary>
    /// Requires exactly one of <c>cellSize</c> (<c>-tr</c>) or <c>width</c>+<c>height</c>
    /// (<c>-ts</c>) so the output grid is unambiguous. The extent is inferred from the
    /// input layer by gdal_rasterize.
    /// </summary>
    private static bool TryBuildGridArgs(
        IReadOnlyDictionary<string, string> parameters,
        GdalWorkerOptions options,
        out List<string> args,
        out string failure)
    {
        args = [];
        failure = "";

        var hasCellSize = GdalJobInputReader.TryGetInput(parameters, "cellSize", out var cellRaw) && !string.IsNullOrWhiteSpace(cellRaw);
        var hasWidth = GdalJobInputReader.TryGetInput(parameters, "width", out var widthRaw) && !string.IsNullOrWhiteSpace(widthRaw);
        var hasHeight = GdalJobInputReader.TryGetInput(parameters, "height", out var heightRaw) && !string.IsNullOrWhiteSpace(heightRaw);

        if (hasCellSize)
        {
            if (hasWidth || hasHeight)
            {
                failure = "supply either 'cellSize' or 'width'+'height', not both";
                return false;
            }
            if (!double.TryParse(cellRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var cell)
                || double.IsNaN(cell) || double.IsInfinity(cell) || cell <= 0d)
            {
                failure = $"'cellSize' must be a positive finite number; got '{cellRaw}'";
                return false;
            }
            var cellLiteral = cell.ToString("R", CultureInfo.InvariantCulture);
            args.Add("-tr");
            args.Add(cellLiteral);
            args.Add(cellLiteral);
            return true;
        }

        if (hasWidth && hasHeight)
        {
            if (!int.TryParse(widthRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) || width < 1
                || !int.TryParse(heightRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height) || height < 1)
            {
                failure = "'width' and 'height' must be positive integers";
                return false;
            }
            // Bound the attacker-controlled OUTPUT grid before gdal_rasterize allocates
            // a width×height output raster, closing the output-allocation OOM vector
            // (#2782). Reuses the same caps the input dimension guard applies (#2766).
            if (!GdalOutputGridGuard.TryAdmit(width, height, options, out failure))
            {
                return false;
            }
            args.Add("-ts");
            args.Add(width.ToString(CultureInfo.InvariantCulture));
            args.Add(height.ToString(CultureInfo.InvariantCulture));
            return true;
        }

        failure = "supply either 'cellSize' or both 'width' and 'height' to define the output grid";
        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(9410, LogLevel.Warning,
            "GDAL rasterize executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9411, LogLevel.Warning,
            "GDAL rasterize executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
