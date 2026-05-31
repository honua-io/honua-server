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
/// Native-profile <see cref="IJobExecutor"/> for the <c>surface.*</c> DEM-derived
/// processes (<c>slope</c>, <c>aspect</c>, <c>hillshade</c>, <c>rugosity-tri</c>,
/// <c>rugosity-tpi</c>, <c>roughness</c>) backed by the GDAL <c>gdaldem</c> CLI.
///
/// One executor handles the whole family because the gdaldem invocation shape is
/// identical (input + output + mode-specific switches); the per-id differences
/// reduce to the subcommand and a handful of switch values. Runs only inside the
/// GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is <c>{ "native" }</c>
/// so the substrate claim fence keeps the lean managed worker from claiming
/// surface.* jobs and keeps this worker from claiming managed jobs.
/// </summary>
public sealed partial class GdalSurfaceJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalSurfaceJobExecutor> logger) : IJobExecutor
{
    /// <summary>Process id for slope.</summary>
    public const string SlopeProcessId = "surface.slope";

    /// <summary>Process id for aspect.</summary>
    public const string AspectProcessId = "surface.aspect";

    /// <summary>Process id for hillshade.</summary>
    public const string HillshadeProcessId = "surface.hillshade";

    /// <summary>Process id for terrain ruggedness index.</summary>
    public const string RugosityTriProcessId = "surface.rugosity-tri";

    /// <summary>Process id for topographic position index.</summary>
    public const string RugosityTpiProcessId = "surface.rugosity-tpi";

    /// <summary>Process id for roughness.</summary>
    public const string RoughnessProcessId = "surface.roughness";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly FrozenSet<string> HandledProcessIds = new HashSet<string>(StringComparer.Ordinal)
    {
        SlopeProcessId,
        AspectProcessId,
        HillshadeProcessId,
        RugosityTriProcessId,
        RugosityTpiProcessId,
        RoughnessProcessId,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <summary>Process ids this executor routes.</summary>
    public static IReadOnlyCollection<string> SupportedProcessIds => HandledProcessIds;

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
                $"Process id '{processId ?? "<none>"}' is not handled by the surface.* executor.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Parsing {processId} inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var inputError))
        {
            Log.InvalidInputs(logger, job.OperationId, inputError);
            return JobExecutionResult.Failed($"Invalid {processId} inputs: {inputError}");
        }

        if (!TryBuildArguments(processId, parameters, out var subcommand, out var modeArgs, out var failure))
        {
            return JobExecutionResult.Failed($"Invalid {processId} inputs: {failure}");
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, $"Running gdaldem {subcommand}", cancellationToken).ConfigureAwait(false);

            // Args layout: subcommand, options, input, output. gdaldem requires
            // the subcommand first; options may go anywhere after. Output is the
            // final argument so the canonical fake runner can write to it
            // without per-subcommand bookkeeping (outputArgIndexFromEnd=0).
            var args = new List<string>
            {
                subcommand,
                "-of", "GTiff",
                "-q",
            };
            args.AddRange(modeArgs);
            args.Add(inputPath);
            args.Add(outputPath);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdaldem", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdaldem timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                // Log raw stderr (operator-facing) but persist only the
                // path-redacted message onto the job's client-visible result.
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, Truncate(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdaldem exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed(
                    "gdaldem reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, $"Encoding {processId} artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdaldem produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Output raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, $"{processId} completed", cancellationToken).ConfigureAwait(false);

            Log.OperationCompleted(logger, job.OperationId, processId, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Maps the canonical surface.* parameters onto the gdaldem subcommand and
    /// switch list. The base 3-arg gdaldem invocation (<c>SUBCOMMAND IN OUT</c>) is
    /// added by the caller; this just returns the subcommand and any mode-specific
    /// switches.
    /// </summary>
    private static bool TryBuildArguments(
        string processId,
        IReadOnlyDictionary<string, string> parameters,
        out string subcommand,
        out List<string> modeArgs,
        out string failure)
    {
        subcommand = "";
        modeArgs = new List<string>();
        failure = "";

        switch (processId)
        {
            case SlopeProcessId:
                subcommand = "slope";
                GdalJobInputReader.TryGetInput(parameters, "units", out var unitsRaw);
                var units = string.IsNullOrWhiteSpace(unitsRaw) ? "degrees" : unitsRaw.Trim();
                if (string.Equals(units, "percent", StringComparison.OrdinalIgnoreCase))
                {
                    modeArgs.Add("-p");
                }
                else if (!string.Equals(units, "degrees", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(units, "degree", StringComparison.OrdinalIgnoreCase))
                {
                    failure = $"unsupported slope units '{unitsRaw}'; allowed values: degrees, percent";
                    return false;
                }
                if (!TryReadPositiveDouble(parameters, "zFactor", defaultValue: 1.0, out var slopeZ, out var zErr))
                {
                    failure = zErr;
                    return false;
                }
                modeArgs.Add("-s");
                modeArgs.Add(FormatDouble(slopeZ));
                return true;

            case AspectProcessId:
                subcommand = "aspect";
                return true;

            case HillshadeProcessId:
                subcommand = "hillshade";
                if (!TryReadDoubleInClosedRange(parameters, "azimuth", defaultValue: 315.0, min: 0.0, max: 360.0, out var azimuth, out var azErr))
                {
                    failure = azErr;
                    return false;
                }
                if (!TryReadDoubleInClosedRange(parameters, "altitude", defaultValue: 45.0, min: 0.0, max: 90.0, out var altitude, out var altErr))
                {
                    failure = altErr;
                    return false;
                }
                if (!TryReadPositiveDouble(parameters, "zFactor", defaultValue: 1.0, out var hillScale, out var hillZErr))
                {
                    failure = hillZErr;
                    return false;
                }
                modeArgs.Add("-az");
                modeArgs.Add(FormatDouble(azimuth));
                modeArgs.Add("-alt");
                modeArgs.Add(FormatDouble(altitude));
                // Catalog declares zFactor as the "vertical-to-horizontal scale
                // factor", which is gdaldem's -s/-scale (unit ratio) — NOT -z
                // (vertical exaggeration). Map to -s so a caller passing the
                // canonical 111120 degrees-to-meters scale gets unit-correct
                // shading instead of extreme vertical exaggeration.
                modeArgs.Add("-s");
                modeArgs.Add(FormatDouble(hillScale));
                return true;

            case RugosityTriProcessId:
                subcommand = "TRI";
                return TryEnforceUnitWindow(parameters, out failure);

            case RugosityTpiProcessId:
                subcommand = "TPI";
                return TryEnforceUnitWindow(parameters, out failure);

            case RoughnessProcessId:
                subcommand = "roughness";
                return TryEnforceUnitWindow(parameters, out failure);

            default:
                failure = $"unsupported surface process id '{processId}'";
                return false;
        }
    }

    /// <summary>
    /// gdaldem TRI/TPI/roughness always operate on a fixed 3x3 neighborhood; the
    /// catalog mirrors that constraint as windowRadius=1. Reject any other value
    /// at the executor boundary so the worker error matches the validator error.
    /// </summary>
    private static bool TryEnforceUnitWindow(
        IReadOnlyDictionary<string, string> parameters,
        out string failure)
    {
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, "windowRadius", out var raw))
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value != 1)
        {
            failure = $"windowRadius must be 1 (3x3 neighborhood); got '{raw}'";
            return false;
        }

        return true;
    }

    private static bool TryReadPositiveDouble(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue,
        out double value,
        out string failure)
    {
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw))
        {
            value = defaultValue;
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed <= 0d)
        {
            value = 0d;
            failure = $"{name} must be a positive finite number; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryReadDoubleInClosedRange(
        IReadOnlyDictionary<string, string> parameters,
        string name,
        double defaultValue,
        double min,
        double max,
        out double value,
        out string failure)
    {
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, name, out var raw))
        {
            value = defaultValue;
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed)
            || parsed < min
            || parsed > max)
        {
            value = 0d;
            failure = $"{name} must be in the closed range [{FormatDouble(min)}, {FormatDouble(max)}]; got '{raw}'";
            return false;
        }

        value = parsed;
        return true;
    }

    private static string FormatDouble(double value)
        => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static partial class Log
    {
        [LoggerMessage(9240, LogLevel.Warning,
            "GDAL surface executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9241, LogLevel.Warning,
            "GDAL surface executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9242, LogLevel.Error,
            "GDAL surface executor failed job {OperationId}: gdaldem exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9243, LogLevel.Error,
            "GDAL surface executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9244, LogLevel.Warning,
            "GDAL surface executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9245, LogLevel.Information,
            "GDAL surface executor completed job {OperationId}: process={ProcessId}, bytes={Bytes}")]
        public static partial void OperationCompleted(ILogger logger, string operationId, string processId, long bytes);
    }

}
