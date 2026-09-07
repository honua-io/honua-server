// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Ordinary-kriging execution for <c>raster.interpolate-kriging</c> (#3932).
///
/// <para>
/// Stock GDAL carries no kriging algorithm, so this operation used to be
/// advertised-but-unavailable. The worker image bundles NumPy next to the GDAL
/// Python bindings, and ordinary kriging is a linear solve, so the numerical
/// backend is <c>Scripts/gdal_ordinary_kriging.py</c>: NumPy solves the kriging
/// system and GDAL owns both I/O boundaries (OGR reads the point layer and its
/// CRS, the GTiff driver writes the georeferenced surface).
/// </para>
/// <para>
/// The published GeoTIFF carries two Float64 bands — band 1 the prediction,
/// band 2 the kriging standard error — so a caller can qualify a prediction
/// instead of trusting it. Ordinary kriging is an exact interpolator: at a
/// sample location the prediction reproduces the sample and the standard error
/// is zero.
/// </para>
/// </summary>
internal sealed partial class GdalRasterInterpolateJobExecutor
{
    /// <summary>Bundled numerical backend that solves the kriging system.</summary>
    internal const string KrigingScript = "gdal_ordinary_kriging.py";

    /// <summary>
    /// Ceiling on sample count. The kriging system is dense and factorised in
    /// O(n^3), so an unbounded point layer is a CPU/memory exhaustion vector on
    /// the shared worker just as an unbounded output grid is (#2782).
    /// </summary>
    internal const int MaxKrigingSamples = 2000;

    /// <summary>Variogram models the bundled backend implements.</summary>
    internal static readonly string[] KrigingVariogramModels = ["spherical", "exponential", "gaussian"];

    private async Task<JobExecutionResult> ExecuteKrigingAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        IReadOnlyDictionary<string, string> parameters,
        GdalWorkerOptions opts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing kriging inputs", cancellationToken).ConfigureAwait(false);

        GdalJobInputReader.TryGetInput(parameters, "zField", out var zField);
        if (!string.IsNullOrWhiteSpace(zField) && !GdalFieldName.IsValid(zField))
        {
            Log.InvalidInputs(logger, job.OperationId, $"'zField' value '{zField}' is not a valid attribute name");
            return JobExecutionResult.Failed(
                "Invalid kriging inputs: 'zField' must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        }

        if (!TryReadOutputSize(parameters, opts, out var width, out var height, out var sizeError))
        {
            Log.InvalidInputs(logger, job.OperationId, sizeError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {sizeError}");
        }

        // Unlike gdal_grid there is no implicit default surface size: kriging
        // solves per target cell, so the caller must state the grid it wants.
        if (width is null || height is null)
        {
            return JobExecutionResult.Failed(
                "Invalid kriging inputs: 'width' and 'height' are required for kriging.");
        }

        if (!TryReadKrigingVariogram(parameters, out var model, out var nugget, out var sill, out var range, out var variogramError))
        {
            Log.InvalidInputs(logger, job.OperationId, variogramError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {variogramError}");
        }

        var noData = "nan";
        if (GdalJobInputReader.TryGetInput(parameters, "nodata", out var noDataRaw) && !string.IsNullOrWhiteSpace(noDataRaw))
        {
            if (!string.Equals(noDataRaw.Trim(), "nan", StringComparison.OrdinalIgnoreCase))
            {
                if (!double.TryParse(noDataRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedNoData)
                    || double.IsInfinity(parsedNoData))
                {
                    return JobExecutionResult.Failed(
                        $"Invalid kriging inputs: 'nodata' must be a finite number or 'nan'; got '{noDataRaw}'.");
                }
                noData = FormatDouble(parsedNoData);
            }
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "points", opts.MaxArtifactBytes, out var pointsBytes, out var pointsError))
        {
            Log.InvalidInputs(logger, job.OperationId, pointsError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {pointsError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Join(workspace, "points.geojson");
            var outputPath = Path.Join(workspace, "output.tif");
            var scriptPath = Path.Join(workspace, KrigingScript);
            await File.WriteAllBytesAsync(inputPath, pointsBytes, cancellationToken).ConfigureAwait(false);
            File.Copy(Path.Join(AppContext.BaseDirectory, "Scripts", KrigingScript), scriptPath, overwrite: true);

            var args = new List<string>
            {
                scriptPath,
                "--points", inputPath,
                "--output", outputPath,
                "--width", width.Value.ToString(CultureInfo.InvariantCulture),
                "--height", height.Value.ToString(CultureInfo.InvariantCulture),
                "--model", model,
                "--nugget", FormatDouble(nugget),
                "--sill", FormatDouble(sill),
                "--range", FormatDouble(range),
                "--nodata", noData,
                "--max-samples", MaxKrigingSamples.ToString(CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(zField))
            {
                args.Add("--z-field");
                args.Add(zField.Trim());
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Solving the ordinary kriging system", cancellationToken).ConfigureAwait(false);
            await GdalCommandLog.LogCommandAsync(context, "python3", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("python3", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"Ordinary kriging timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"Ordinary kriging exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            {
                return JobExecutionResult.Failed("Ordinary kriging reported success but produced no output raster.");
            }

            var outputLength = new FileInfo(outputPath).Length;
            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding kriged raster artifact", cancellationToken).ConfigureAwait(false);

            var publishError = await GdalArtifactPublisher.PublishFileAsync(
                context, opts, logger, job.OperationId, outputPath, GeoTiffContentType,
                "Kriged raster", cancellationToken).ConfigureAwait(false);
            if (publishError is not null)
            {
                return JobExecutionResult.Failed(publishError);
            }

            await context.ReportProgressAsync(100, "Kriging completed", cancellationToken).ConfigureAwait(false);
            Log.InterpolationCompleted(logger, job.OperationId, outputLength);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Reads and range-checks the frozen variogram configuration. <c>sill</c> is
    /// the TOTAL sill, so a model is only well posed when
    /// <c>0 &lt;= nugget &lt; sill</c> and the range is positive.
    /// </summary>
    private static bool TryReadKrigingVariogram(
        IReadOnlyDictionary<string, string> parameters,
        out string model,
        out double nugget,
        out double sill,
        out double range,
        out string failure)
    {
        model = "spherical";
        nugget = 0d;
        sill = 1d;
        range = 0d;
        failure = "";

        if (GdalJobInputReader.TryGetInput(parameters, "variogramModel", out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            var candidate = raw.Trim().ToLowerInvariant();
            if (Array.IndexOf(KrigingVariogramModels, candidate) < 0)
            {
                failure = $"'variogramModel' must be one of {string.Join(", ", KrigingVariogramModels)}; got '{raw}'";
                return false;
            }
            model = candidate;
        }

        if (!TryReadDouble(parameters, "nugget", defaultValue: 0d, requirePositive: false, out nugget, out var nuggetError))
        {
            failure = nuggetError;
            return false;
        }

        if (!TryReadDouble(parameters, "sill", defaultValue: 1d, requirePositive: true, out sill, out var sillError))
        {
            failure = sillError;
            return false;
        }

        if (!TryReadDouble(parameters, "range", defaultValue: 0d, requirePositive: true, out range, out var rangeError))
        {
            failure = rangeError;
            return false;
        }

        if (range <= 0d)
        {
            failure = "'range' is required and must be a positive finite number";
            return false;
        }

        if (sill <= nugget)
        {
            failure = $"the variogram needs nugget < sill; got nugget={FormatDouble(nugget)} and sill={FormatDouble(sill)}";
            return false;
        }

        return true;
    }
}
