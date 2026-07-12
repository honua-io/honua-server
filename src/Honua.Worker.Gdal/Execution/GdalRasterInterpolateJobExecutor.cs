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
/// family: <c>raster.interpolate-idw</c> (inverse-distance-weighted) backed by the
/// GDAL <c>gdal_grid</c> CLI, and <c>raster.interpolate-kriging</c>, which is
/// FLAGGED as unsupported in this build.
///
/// <para>
/// IDW reads a base64-encoded GeoJSON point FeatureCollection from <c>points</c>,
/// grids it onto a raster surface using <c>gdal_grid -a invdist</c>, and publishes
/// the GeoTIFF as a canonical data-URI artifact.
/// </para>
/// <para>
/// Kriging requires a kriging-capable numerical backend that the worker image does
/// NOT bundle (stock GDAL <c>gdal_grid</c> has no kriging algorithm). Rather than
/// silently substitute a different algorithm, the executor FAILS the job with a
/// clear message so the limitation is explicit (#2141).
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

    /// <summary>Process id for kriging interpolation (flagged unsupported).</summary>
    public const string KrigingProcessId = "raster.interpolate-kriging";

    /// <summary>
    /// Stable message published when a kriging job is submitted. Surfaced as the
    /// job's failure reason so callers see the unsupported-dependency limitation
    /// rather than a silent no-op or a substituted algorithm.
    /// </summary>
    public const string KrigingUnsupportedMessage =
        "Kriging interpolation is not available in this build: the worker image does not bundle a "
        + "kriging-capable numerical backend (stock GDAL gdal_grid has no kriging algorithm). "
        + "Use raster.interpolate-idw for inverse-distance-weighted interpolation.";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

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

        // Kriging is advertised but flagged: fail fast with a clear message instead
        // of substituting IDW or producing a silent stub (#2141 acceptance).
        if (string.Equals(processId, KrigingProcessId, StringComparison.Ordinal))
        {
            Log.KrigingUnsupported(logger, job.OperationId);
            return JobExecutionResult.Failed(KrigingUnsupportedMessage);
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
            var inputPath = Path.Combine(workspace, "points.geojson");
            var outputPath = Path.Combine(workspace, "output.tif");
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

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdal_grid produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Interpolated raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Interpolation completed", cancellationToken).ConfigureAwait(false);

            Log.InterpolationCompleted(logger, job.OperationId, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
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

        var spec = $"invdist:power={FormatDouble(power)}:smoothing={FormatDouble(smoothing)}";

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

        [LoggerMessage(9324, LogLevel.Warning,
            "GDAL raster interpolate executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9325, LogLevel.Information,
            "GDAL raster interpolate executor completed job {OperationId}: bytes={Bytes}")]
        public static partial void InterpolationCompleted(ILogger logger, string operationId, long bytes);

        [LoggerMessage(9326, LogLevel.Warning,
            "GDAL raster interpolate executor refused job {OperationId}: kriging is flagged unsupported in this build")]
        public static partial void KrigingUnsupported(ILogger logger, string operationId);
    }
}
