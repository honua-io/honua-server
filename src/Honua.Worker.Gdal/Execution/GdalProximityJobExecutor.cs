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
/// (#2240, #2255): <c>proximity.euclidean-distance</c>, a distance-to-nearest-source
/// raster backed by the GDAL <c>gdal_proximity.py</c> CLI, and
/// <c>proximity.euclidean-allocation</c>, the nearest-source allocation companion.
///
/// <para>
/// Distance reads a base64-encoded source GeoTIFF whose non-zero (or
/// <c>values</c>-listed) pixels are the proximity targets, and publishes a Float32
/// distance raster honoring the requested distance units and optional maximum
/// distance.
/// </para>
/// <para>
/// Allocation (assigning each cell the VALUE/id of its nearest source — a discrete
/// Voronoi tessellation) has NO equivalent in stock <c>gdal_proximity.py</c>, which
/// computes distance only. It is implemented as a small custom worker step
/// (<c>Scripts/gdal_euclidean_allocation.py</c>, #2255) layered on the GDAL Python
/// bindings plus SciPy's exact Euclidean distance transform with nearest-feature
/// index return. The output GeoTIFF preserves the source extent, cell size, CRS and
/// band data type.
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

    /// <summary>Process id for the Euclidean allocation (nearest-source) raster.</summary>
    public const string AllocationProcessId = "proximity.euclidean-allocation";

    /// <summary>
    /// Name of the bundled custom worker step implementing Euclidean allocation. The
    /// script ships in the published worker's <c>Scripts</c> folder and is invoked via
    /// <c>python3</c>; it requires the GDAL Python bindings + SciPy from the worker
    /// image.
    /// </summary>
    public const string AllocationScriptName = "gdal_euclidean_allocation.py";

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

        var isAllocation = string.Equals(processId, AllocationProcessId, StringComparison.Ordinal);

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
        if (GdalJobInputReader.TryGetInput(parameters, "values", out var valuesRaw) && !string.IsNullOrWhiteSpace(valuesRaw)
            && !TryNormalizeValues(valuesRaw, out values, out var valuesError))
        {
            return JobExecutionResult.Failed($"Invalid proximity inputs: {valuesError}");
        }

        if (!GdalJobInputReader.TryGetRasterInput(parameters, "source", opts.MaxArtifactBytes, out var sourceInput, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid proximity inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Both second segments are fixed relative literal filenames, so they can
            // never be rooted and silently discard workspace.
            var inputPath = sourceInput.ReferencedPath ?? Path.Join(workspace, "input.tif");
            var outputPath = Path.Join(workspace, "output.tif");
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

            if (isAllocation)
            {
                // Custom worker step: stock gdal_proximity has no nearest-source
                // allocation mode. python3 Scripts/gdal_euclidean_allocation.py SRC DST
                // [--band 1] --dist-units U [--max-distance D] [--values v,...] — keep
                // the positional src/dst pair first so the optional flags follow.
                // "Scripts" and AllocationScriptName are both fixed relative literals
                // (the latter a private const), so neither can be rooted and silently
                // discard AppContext.BaseDirectory.
                var scriptPath = Path.Join(AppContext.BaseDirectory, "Scripts", AllocationScriptName);
                var allocArgs = new List<string>
                {
                    scriptPath,
                    inputPath,
                    outputPath,
                    "--dist-units", distUnits,
                };
                if (maxDistance.HasValue)
                {
                    allocArgs.Add("--max-distance");
                    allocArgs.Add(maxDistance.Value.ToString("R", CultureInfo.InvariantCulture));
                }
                if (values is not null)
                {
                    allocArgs.Add("--values");
                    allocArgs.Add(values);
                }
                if (!string.IsNullOrWhiteSpace(sourceInput.ExpectedETag))
                {
                    allocArgs.Add("--http-if-match");
                    allocArgs.Add(sourceInput.ExpectedETag);
                }

                cancellationToken.ThrowIfCancellationRequested();
                await context.ReportProgressAsync(40, "Running euclidean allocation", cancellationToken).ConfigureAwait(false);

                return await GdalToolExecution.RunAndPublishAsync(
                    runner, context, opts, logger, job.OperationId,
                    "python3", allocArgs, workspace, outputPath,
                    GeoTiffContentType, "Allocation raster",
                    "Encoding allocation raster artifact", "Allocation completed",
                    cancellationToken).ConfigureAwait(false);
            }

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
            sourceInput.AddReadPin(args);
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
    }
}
