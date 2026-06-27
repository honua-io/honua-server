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
/// Native-profile <see cref="IProcessExecutor"/> for the Euclidean-proximity family
/// (#2240): <c>proximity.euclidean-distance</c>, a distance-to-nearest-source raster
/// backed by the GDAL <c>gdal_proximity.py</c> CLI, and
/// <c>proximity.euclidean-allocation</c>, which is FLAGGED unsupported in this build.
///
/// <para>
/// Distance reads a base64-encoded source GeoTIFF whose non-zero (or
/// <c>values</c>-listed) pixels are the proximity targets, and publishes a Float32
/// distance raster honoring the requested distance units and optional maximum
/// distance.
/// </para>
/// <para>
/// Allocation (assigning each cell the value of its nearest source) has NO equivalent
/// in stock <c>gdal_proximity.py</c> — the CLI computes distance only and has no
/// nearest-source allocation mode. Rather than silently substitute a distance raster
/// or a fixed-buffer value, the executor FAILS the job with a clear message so the
/// limitation is explicit, mirroring the flagged-kriging contract (#2141).
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalProximityJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalProximityJobExecutor> logger) : IProcessExecutor
{
    /// <summary>Process id for the Euclidean distance raster.</summary>
    public const string DistanceProcessId = "proximity.euclidean-distance";

    /// <summary>Process id for Euclidean allocation (flagged unsupported).</summary>
    public const string AllocationProcessId = "proximity.euclidean-allocation";

    /// <summary>
    /// Stable failure message published when an allocation job is submitted. Stock
    /// <c>gdal_proximity.py</c> has no nearest-source allocation mode.
    /// </summary>
    public const string AllocationUnsupportedMessage =
        "Euclidean allocation is not available in this build: stock GDAL gdal_proximity computes distance only and "
        + "has no nearest-source allocation mode. Use proximity.euclidean-distance for the distance raster.";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly FrozenSet<string> HandledProcessIds = new HashSet<string>(StringComparer.Ordinal)
    {
        DistanceProcessId,
        AllocationProcessId,
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    private static readonly FrozenSet<string> DistanceUnits = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "GEO",
        "PIXEL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
                $"Process id '{processId ?? "<none>"}' is not handled by the proximity executor.");
        }

        if (string.Equals(processId, AllocationProcessId, StringComparison.Ordinal))
        {
            Log.AllocationUnsupported(logger, job.OperationId);
            return JobExecutionResult.Failed(AllocationUnsupportedMessage);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, "Parsing proximity inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        var distUnits = "GEO";
        if (GdalJobInputReader.TryGetInput(parameters, "distUnits", out var distUnitsRaw)
            && !string.IsNullOrWhiteSpace(distUnitsRaw))
        {
            if (!DistanceUnits.Contains(distUnitsRaw.Trim()))
            {
                return JobExecutionResult.Failed(
                    $"Invalid proximity inputs: 'distUnits' value '{distUnitsRaw}' is not in the allowed set (GEO, PIXEL).");
            }
            distUnits = distUnitsRaw.Trim().ToUpperInvariant();
        }

        double? maxDistance = null;
        if (GdalJobInputReader.TryGetInput(parameters, "maxDistance", out var maxRaw) && !string.IsNullOrWhiteSpace(maxRaw))
        {
            if (!TryParsePositive(maxRaw, out var parsed))
            {
                return JobExecutionResult.Failed(
                    $"Invalid proximity inputs: 'maxDistance' must be a positive finite number; got '{maxRaw}'.");
            }
            maxDistance = parsed;
        }

        string? values = null;
        if (GdalJobInputReader.TryGetInput(parameters, "values", out var valuesRaw) && !string.IsNullOrWhiteSpace(valuesRaw))
        {
            if (!TryNormalizeValues(valuesRaw, out values, out var valuesError))
            {
                return JobExecutionResult.Failed($"Invalid proximity inputs: {valuesError}");
            }
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid proximity inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            // gdal_proximity.py [options] srcfile dstfile — keep the positional
            // src/dst pair last so the output path is the final argument.
            var args = new List<string>
            {
                "-of", "GTiff",
                "-ot", "Float32",
                "-distunits", distUnits,
            };
            if (maxDistance.HasValue)
            {
                args.Add("-maxdist");
                args.Add(maxDistance.Value.ToString("R", CultureInfo.InvariantCulture));
            }
            if (values is not null)
            {
                args.Add("-values");
                args.Add(values);
            }
            args.Add(inputPath);
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_proximity", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_proximity.py", args, workspace, outputPath,
                GeoTiffContentType, "Proximity raster",
                "Encoding proximity raster artifact", "Proximity completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Validates and re-serializes the comma-separated target pixel value list so
    /// only finite integers reach the <c>-values</c> flag.
    /// </summary>
    private static bool TryNormalizeValues(string raw, out string? values, out string failure)
    {
        values = null;
        failure = "";
        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            failure = "'values' must list one or more comma-separated integer pixel values";
            return false;
        }

        var normalized = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                failure = $"'values' entry '{part}' is not an integer";
                return false;
            }
            normalized.Add(value.ToString(CultureInfo.InvariantCulture));
        }

        values = string.Join(',', normalized);
        return true;
    }

    private static bool TryParsePositive(string raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value) && value > 0d;

    private static partial class Log
    {
        [LoggerMessage(9370, LogLevel.Warning,
            "GDAL proximity executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9371, LogLevel.Warning,
            "GDAL proximity executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9372, LogLevel.Warning,
            "GDAL proximity executor refused job {OperationId}: euclidean allocation is flagged unsupported in this build")]
        public static partial void AllocationUnsupported(ILogger logger, string operationId);
    }
}
