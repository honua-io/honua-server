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
/// Native-profile <see cref="IProcessExecutor"/> for the raster interpolation
/// family: <c>raster.interpolate-idw</c> (inverse-distance-weighted) and
/// <c>raster.interpolate-kriging</c> (ordinary kriging).
///
/// <para>
/// IDW reads a base64-encoded GeoJSON point FeatureCollection from <c>points</c>,
/// grids it onto a raster surface using <c>gdal_grid -a invdist</c>, and publishes
/// the GeoTIFF as a canonical data-URI artifact.
/// </para>
/// <para>
/// Kriging reads the same payload and predicts the surface with the bundled
/// <see cref="OrdinaryKriging"/> solver, because stock GDAL <c>gdal_grid</c> has no
/// kriging algorithm (#3932). The prediction is numerical work the worker does itself;
/// the raster is still materialized by the pinned GDAL toolchain via
/// <c>gdal_translate</c>, so the published artifact comes off the same production path
/// as every other native raster op.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/>
/// is <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterInterpolateJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterInterpolateJobExecutor> logger) : IProcessExecutor
{
    /// <summary>Process id for inverse-distance-weighted interpolation.</summary>
    public const string IdwProcessId = "raster.interpolate-idw";

    /// <summary>Process id for ordinary-kriging interpolation.</summary>
    public const string KrigingProcessId = "raster.interpolate-kriging";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    /// <summary>
    /// Output grid size used when the caller pins neither <c>width</c> nor <c>height</c>,
    /// matching <c>gdal_grid</c>'s own 256×256 default so IDW and kriging agree.
    /// </summary>
    private const int DefaultGridSize = 256;

    /// <summary>
    /// CRS assigned to the kriging output when the caller does not name one. GeoJSON is
    /// WGS 84 lon/lat by definition (RFC 7946), which is also what OGR reports for the
    /// point layer <c>gdal_grid</c> reads on the IDW path.
    /// </summary>
    private const string DefaultSrid = "EPSG:4326";

    private static readonly FrozenSet<string> HandledProcessIds = new HashSet<string>(StringComparer.Ordinal)
    {
        IdwProcessId,
        KrigingProcessId,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <summary>Process ids this executor routes.</summary>
    public static IReadOnlyCollection<string> SupportedProcessIds => HandledProcessIds;

    /// <inheritdoc />
    public IReadOnlySet<string> ProcessIds => HandledProcessIds;

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
        if (processId is null || !HandledProcessIds.Contains(processId))
        {
            Log.UnsupportedProcessId(logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the raster interpolation executor.");
        }

        if (string.Equals(processId, KrigingProcessId, StringComparison.Ordinal))
        {
            return await ExecuteKrigingAsync(job, context, cancellationToken).ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing interpolation inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!TryBuildAlgorithm(parameters, out var algorithm, out var algorithmError))
        {
            return JobExecutionResult.Failed($"Invalid interpolation inputs: {algorithmError}");
        }

        if (!TryReadOutputSize(parameters, opts, out var width, out var height, out var sizeError))
        {
            return JobExecutionResult.Failed($"Invalid interpolation inputs: {sizeError}");
        }

        GdalJobInputReader.TryGetInput(parameters, "zField", out var zField);
        if (!string.IsNullOrWhiteSpace(zField) && !GdalFieldName.IsValid(zField))
        {
            Log.InvalidInputs(logger, job.OperationId, $"'zField' value '{zField}' is not a valid attribute name");
            return JobExecutionResult.Failed(
                "Invalid interpolation inputs: 'zField' must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "points", opts.MaxArtifactBytes, out var pointsBytes, out var pointsError))
        {
            Log.InvalidInputs(logger, job.OperationId, pointsError);
            return JobExecutionResult.Failed($"Invalid interpolation inputs: {pointsError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // gdal_grid derives the layer name from the file basename; write the
            // input as points.geojson and reference layer "points" explicitly.
            // Both second segments are fixed relative literal filenames, so they can
            // never be rooted and silently discard workspace.
            var inputPath = Path.Join(workspace, "points.geojson");
            var outputPath = Path.Join(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, pointsBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_grid interpolation", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-a", algorithm,
                "-of", "GTiff",
                "-l", "points",
            };
            if (!string.IsNullOrWhiteSpace(zField))
            {
                args.Add("-zfield");
                args.Add(zField.Trim());
            }
            if (width.HasValue && height.HasValue)
            {
                args.Add("-outsize");
                args.Add(width.Value.ToString(CultureInfo.InvariantCulture));
                args.Add(height.Value.ToString(CultureInfo.InvariantCulture));
            }
            args.Add(inputPath);
            args.Add(outputPath);

            await GdalCommandLog.LogCommandAsync(context, "gdal_grid", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdal_grid", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdal_grid timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdal_grid exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdal_grid reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding interpolated raster artifact", cancellationToken).ConfigureAwait(false);

            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength == 0)
            {
                return JobExecutionResult.Failed("gdal_grid produced an empty output raster.");
            }

            var publishError = await GdalArtifactPublisher.PublishFileAsync(
                context, opts, logger, job.OperationId, outputPath, GeoTiffContentType,
                "Interpolated raster", cancellationToken).ConfigureAwait(false);
            if (publishError is not null)
            {
                return JobExecutionResult.Failed(publishError);
            }

            await context.ReportProgressAsync(100, "Interpolation completed", cancellationToken).ConfigureAwait(false);

            Log.InterpolationCompleted(logger, job.OperationId, outputLength);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Executes <c>raster.interpolate-kriging</c>: ordinary kriging over the scattered
    /// <c>points</c> payload, predicted at the centres of a width×height grid spanning
    /// the sample extent, then materialized as a GeoTIFF by the pinned GDAL toolchain.
    ///
    /// <para>
    /// The predictor is solved once in the dual formulation (<see cref="OrdinaryKriging"/>),
    /// so the whole surface costs one O(n³) factorization plus O(n) per cell — bounded by
    /// <see cref="GdalWorkerOptions.MaxKrigingSamples"/> on the sample side and by the
    /// shared <see cref="GdalOutputGridGuard"/> caps on the grid side.
    /// </para>
    /// </summary>
    private async Task<JobExecutionResult> ExecuteKrigingAsync(
        ExecutionJobRecord job,
        IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var parameters = job.Spec.Parameters;
        var opts = options.CurrentValue;

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing kriging inputs", cancellationToken).ConfigureAwait(false);

        GdalJobInputReader.TryGetInput(parameters, "zField", out var zField);
        if (!string.IsNullOrWhiteSpace(zField) && !GdalFieldName.IsValid(zField))
        {
            Log.InvalidInputs(logger, job.OperationId, $"'zField' value '{zField}' is not a valid attribute name");
            return JobExecutionResult.Failed(
                "Invalid kriging inputs: 'zField' must match ^[A-Za-z_][A-Za-z0-9_]*$.");
        }

        GdalJobInputReader.TryGetInput(parameters, "model", out var modelRaw);
        if (!OrdinaryKriging.TryParseModel(modelRaw, out var model))
        {
            Log.InvalidInputs(logger, job.OperationId, $"'model' value '{modelRaw}' is not an authorized variogram model");
            return JobExecutionResult.Failed(
                "Invalid kriging inputs: 'model' must be one of spherical, exponential, gaussian.");
        }

        if (!TryReadOptionalDouble(parameters, "nugget", requirePositive: false, out var nugget, out var nuggetError)
            || !TryReadOptionalDouble(parameters, "sill", requirePositive: true, out var sill, out var sillError)
            || !TryReadOptionalDouble(parameters, "range", requirePositive: true, out var range, out var rangeError))
        {
            var error = nuggetError.Length > 0 ? nuggetError : sillError.Length > 0 ? sillError : rangeError;
            Log.InvalidInputs(logger, job.OperationId, error);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {error}");
        }

        if (!TryReadOutputSize(parameters, opts, out var requestedWidth, out var requestedHeight, out var sizeError))
        {
            Log.InvalidInputs(logger, job.OperationId, sizeError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {sizeError}");
        }

        var width = requestedWidth ?? DefaultGridSize;
        var height = requestedHeight ?? DefaultGridSize;

        // Kriging materializes the whole prediction surface in memory before handing it
        // to GDAL, so it carries a tighter cell cap than the shared output-grid guard.
        if ((long)width * height > opts.MaxKrigingCells)
        {
            var cellError = $"requested output grid {width.ToString(CultureInfo.InvariantCulture)}×{height.ToString(CultureInfo.InvariantCulture)} "
                + $"exceeds configured MaxKrigingCells={opts.MaxKrigingCells.ToString(CultureInfo.InvariantCulture)}";
            Log.InvalidInputs(logger, job.OperationId, cellError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {cellError}");
        }

        var srid = DefaultSrid;
        if (GdalJobInputReader.TryGetInput(parameters, "srid", out var sridRaw) && !string.IsNullOrWhiteSpace(sridRaw))
        {
            if (!GdalSrsToken.IsValid(sridRaw))
            {
                Log.InvalidInputs(logger, job.OperationId, $"'srid' value '{sridRaw}' is not an accepted CRS token");
                return JobExecutionResult.Failed(
                    "Invalid kriging inputs: 'srid' must be an EPSG code or a short AUTHORITY:CODE token.");
            }

            srid = GdalSrsToken.Normalize(sridRaw);
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "points", opts.MaxArtifactBytes, out var pointsBytes, out var pointsError))
        {
            Log.InvalidInputs(logger, job.OperationId, pointsError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {pointsError}");
        }

        if (!KrigingGridInputs.TryReadSamples(pointsBytes, zField, opts.MaxKrigingSamples, out var samples, out var samplesError))
        {
            Log.InvalidInputs(logger, job.OperationId, samplesError);
            return JobExecutionResult.Failed($"Invalid kriging inputs: {samplesError}");
        }

        var variogram = OrdinaryKriging.FitDefaults(samples, model, nugget, sill, range);
        if (variogram.Sill < variogram.Nugget)
        {
            Log.InvalidInputs(logger, job.OperationId, "'sill' is below 'nugget'");
            return JobExecutionResult.Failed(
                "Invalid kriging inputs: 'sill' is the TOTAL sill and must be greater than or equal to 'nugget'.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(35, "Solving the ordinary kriging system", cancellationToken)
            .ConfigureAwait(false);

        if (!OrdinaryKriging.TrySolve(samples, variogram, out var kriging, out var solveError))
        {
            Log.InvalidInputs(logger, job.OperationId, solveError);
            return JobExecutionResult.Failed($"Kriging failed: {solveError}.");
        }

        await context.ReportProgressAsync(55, "Predicting the interpolated surface", cancellationToken)
            .ConfigureAwait(false);

        var grid = KrigingGridInputs.BuildGrid(samples, width, height);
        var values = new double[width * height];
        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var y = grid.CentreY(row);
            for (var column = 0; column < width; column++)
            {
                var prediction = kriging.Predict(grid.CentreX(column), y);
                if (!double.IsFinite(prediction))
                {
                    // A non-finite prediction means the solve degenerated numerically.
                    // Fail the job rather than writing a hole that reads as real data.
                    Log.KrigingPredictionDiverged(logger, job.OperationId, column, row);
                    return JobExecutionResult.Failed(
                        "Kriging failed: the fitted variogram produced a non-finite prediction; "
                        + "supply an explicit 'range'/'sill' or raise 'nugget'.");
                }

                values[(row * width) + column] = prediction;
            }
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Both second segments are fixed relative literal filenames, so they can
            // never be rooted and silently discard workspace.
            var gridPath = Path.Join(workspace, "kriging.bil");
            var outputPath = Path.Join(workspace, "output.tif");
            await KrigingGridInputs.WriteGridAsync(gridPath, grid, values, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(75, "Encoding the interpolated raster", cancellationToken)
                .ConfigureAwait(false);

            var args = new List<string> { "-of", "GTiff", "-a_srs", srid, gridPath, outputPath };
            await GdalCommandLog.LogCommandAsync(context, "gdal_translate", args, workspace, cancellationToken)
                .ConfigureAwait(false);

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
                Log.KrigingToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdal_translate exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdal_translate reported success but produced no output raster.");
            }

            var outputLength = new FileInfo(outputPath).Length;
            if (outputLength == 0)
            {
                return JobExecutionResult.Failed("gdal_translate produced an empty output raster.");
            }

            var publishError = await GdalArtifactPublisher.PublishFileAsync(
                context, opts, logger, job.OperationId, outputPath, GeoTiffContentType,
                "Interpolated raster", cancellationToken).ConfigureAwait(false);
            if (publishError is not null)
            {
                return JobExecutionResult.Failed(publishError);
            }

            await context.ReportProgressAsync(100, "Interpolation completed", cancellationToken).ConfigureAwait(false);

            Log.InterpolationCompleted(logger, job.OperationId, outputLength);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Reads an optional finite tuning parameter, returning <see langword="null"/> when the
    /// caller omitted it so the variogram fit can supply its data-derived default.
    /// </summary>
    private static bool TryReadOptionalDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        bool requirePositive,
        out double? value,
        out string failure)
    {
        value = null;
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!TryReadDouble(parameters, name, defaultValue: 0d, requirePositive, out var parsed, out failure))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    /// <summary>
    /// Builds the <c>gdal_grid -a</c> algorithm spec for IDW. The parameter names
    /// mirror gdal_grid's <c>invdist</c> options: power (default 2.0), smoothing
    /// (default 0.0), and an optional search radius (omitted = global). Each value
    /// is range-checked here so a plan that reaches the worker is also accepted by
    /// the CLI rather than failing at the argument boundary.
    /// </summary>
    private static bool TryBuildAlgorithm(
        IReadOnlyDictionary<string, string> parameters,
        out string algorithm,
        out string failure)
    {
        algorithm = "";
        failure = "";

        if (!TryReadDouble(parameters, "power", defaultValue: 2.0, requirePositive: true, out var power, out var powerError))
        {
            failure = powerError;
            return false;
        }

        if (!TryReadDouble(parameters, "smoothing", defaultValue: 0.0, requirePositive: false, out var smoothing, out var smoothingError))
        {
            failure = smoothingError;
            return false;
        }

        // Explicit NaN both marks empty searches and preserves legitimate zero
        // source/interpolated values. An omitted nodata option fills holes with
        // zero without declaring band nodata metadata.
        var spec = $"invdist:power={FormatDouble(power)}:smoothing={FormatDouble(smoothing)}:nodata=nan";

        if (GdalJobInputReader.TryGetInput(parameters, "radius", out var radiusRaw)
            && !string.IsNullOrWhiteSpace(radiusRaw))
        {
            if (!TryReadDouble(parameters, "radius", defaultValue: 0.0, requirePositive: true, out var radius, out var radiusError))
            {
                failure = radiusError;
                return false;
            }
            spec += $":radius={FormatDouble(radius)}";
        }

        algorithm = spec;
        return true;
    }

    private static bool TryReadOutputSize(
        IReadOnlyDictionary<string, string> parameters,
        GdalWorkerOptions options,
        out int? width,
        out int? height,
        out string failure)
    {
        width = null;
        height = null;
        failure = "";

        var hasWidth = TryReadOptionalPositiveInt(parameters, "width", out width, out var widthError);
        if (!hasWidth && widthError.Length > 0)
        {
            failure = widthError;
            return false;
        }

        var hasHeight = TryReadOptionalPositiveInt(parameters, "height", out height, out var heightError);
        if (!hasHeight && heightError.Length > 0)
        {
            failure = heightError;
            return false;
        }

        // gdal_grid -outsize requires BOTH dimensions; reject a half-specified grid
        // so the contract is unambiguous (omit both to let gdal_grid pick a default).
        if (width.HasValue ^ height.HasValue)
        {
            failure = "both 'width' and 'height' must be supplied together to set the output grid size";
            width = null;
            height = null;
            return false;
        }

        // Bound the attacker-controlled OUTPUT grid before gdal_grid allocates a
        // width×height surface, closing the output-allocation OOM vector (#2782).
        // Reuses the same caps the input dimension guard applies (#2766).
        if (width.HasValue && height.HasValue
            && !GdalOutputGridGuard.TryAdmit(width.Value, height.Value, options, out failure))
        {
            width = null;
            height = null;
            return false;
        }

        return true;
    }

    private static bool TryReadOptionalPositiveInt(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        out int? value,
        out string failure)
    {
        value = null;
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
        {
            failure = $"{name} must be a positive integer; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue,
        bool requirePositive,
        out double value,
        out string failure)
    {
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            value = defaultValue;
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || (requirePositive ? parsed <= 0d : parsed < 0d))
        {
            value = 0d;
            failure = requirePositive
                ? $"{name} must be a positive finite number; got '{raw}'"
                : $"{name} must be a non-negative finite number; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatDouble(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static partial class Log
    {
        [LoggerMessage(9320, LogLevel.Warning,
            "GDAL raster interpolate executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9321, LogLevel.Warning,
            "GDAL raster interpolate executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9322, LogLevel.Error,
            "GDAL raster interpolate executor failed job {OperationId}: gdal_grid exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9323, LogLevel.Error,
            "GDAL raster interpolate executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9325, LogLevel.Information,
            "GDAL raster interpolate executor completed job {OperationId}: bytes={Bytes}")]
        public static partial void InterpolationCompleted(ILogger logger, string operationId, long bytes);

        [LoggerMessage(9326, LogLevel.Error,
            "GDAL raster interpolate executor failed job {OperationId}: kriging prediction diverged at cell ({Column},{Row})")]
        public static partial void KrigingPredictionDiverged(ILogger logger, string operationId, int column, int row);

        [LoggerMessage(9327, LogLevel.Error,
            "GDAL raster interpolate executor failed job {OperationId}: gdal_translate exit {ExitCode}: {Error}")]
        public static partial void KrigingToolFailed(ILogger logger, string operationId, int exitCode, string error);
    }
}
