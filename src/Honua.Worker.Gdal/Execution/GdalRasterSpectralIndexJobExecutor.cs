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
/// Native-profile <see cref="IProcessExecutor"/> for the <c>raster.spectral-index</c>
/// process: computes a named spectral index (NDVI, NDWI, NDBI, SAVI, EVI) from
/// band-role rasters, compiling down to the <c>gdal_calc.py</c> map-algebra core
/// (#2239).
///
/// <para>
/// Each required band role (<c>red</c>, <c>nir</c>, <c>green</c>, <c>swir</c>,
/// <c>blue</c>) is supplied as its own base64-encoded single-band GeoTIFF. The
/// executor binds the roles needed by the requested index to band variables and
/// builds a TRUSTED calc expression (constructed here, never user-supplied), forcing
/// a <c>Float32</c> output so the normalized-difference ratios are not truncated.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterSpectralIndexJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterSpectralIndexJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.spectral-index";

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    /// <summary>Spectral index presets keyed by canonical name.</summary>
    public static readonly FrozenSet<string> SupportedIndices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ndvi", "ndwi", "ndbi", "savi", "evi",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
        await context.ReportProgressAsync(5, "Parsing spectral-index inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "index", out var indexRaw)
            || string.IsNullOrWhiteSpace(indexRaw))
        {
            return JobExecutionResult.Failed("Invalid spectral-index inputs: missing required input 'index'.");
        }

        var index = indexRaw.Trim().ToLowerInvariant();
        if (!SupportedIndices.Contains(index))
        {
            return JobExecutionResult.Failed(
                $"Invalid spectral-index inputs: 'index' value '{indexRaw}' is not supported (NDVI, NDWI, NDBI, SAVI, EVI).");
        }

        // Each preset declares the ordered band roles it needs (bound to A, B, C…)
        // and a builder for the trusted calc expression.
        if (!TryBuildIndex(index, parameters, out var roles, out var calc, out var buildError))
        {
            return JobExecutionResult.Failed($"Invalid spectral-index inputs: {buildError}");
        }

        if (!GdalNoData.TryReadExplicitNoData(parameters, out var explicitNoData, out var noDataError))
        {
            return JobExecutionResult.Failed($"Invalid spectral-index inputs: {noDataError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Second segment is a fixed relative literal filename, so it can never be
            // rooted and silently discard workspace.
            var outputPath = Path.Join(workspace, "output.tif");
            var firstInputPath = "";
            var args = new List<string>(roles.Count * 2 + 8);
            for (var i = 0; i < roles.Count; i++)
            {
                var letter = (char)('A' + i);
                if (!GdalJobInputReader.TryGetRasterInput(parameters, roles[i], opts.MaxArtifactBytes, out var rasterInput, out var roleError))
                {
                    Log.InvalidInputs(logger, job.OperationId, roleError);
                    return JobExecutionResult.Failed($"Invalid spectral-index inputs: {roleError}");
                }

                // Bound the DECLARED pixel footprint before writing the file or
                // invoking GDAL so a compressible GeoTIFF declaring enormous
                // dimensions cannot force a decompression-bomb allocation (#2766).
                if (!rasterInput.TryAdmit(opts, out var dimensionError))
                {
                    Log.InvalidInputs(logger, job.OperationId, dimensionError);
                    return JobExecutionResult.Failed($"Invalid spectral-index inputs: {dimensionError}");
                }

                // Second segment is built from roles[i], which is always one of the
                // fixed band-role literals ("red", "nir", "green", "swir", "blue")
                // assigned by TryBuildIndex's switch above (never user-supplied), so
                // it can never be rooted and silently discard workspace.
                var inputPath = Path.Join(workspace, $"{roles[i]}.tif");
                if (rasterInput.InlineBytes is { } inlineBytes)
                {
                    await File.WriteAllBytesAsync(inputPath, inlineBytes, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    // gdal_calc accepts one process-wide HTTP header, while spectral
                    // indices can bind several independently pinned objects. Stage each
                    // source through a conditional GDAL read inside the worker so every
                    // object gets its own If-Match check and no payload enters the web job.
                    var stageArgs = new List<string> { "-of", "GTiff" };
                    rasterInput.AddReadPin(stageArgs);
                    stageArgs.Add(rasterInput.ReferencedPath!);
                    stageArgs.Add(inputPath);

                    using var stageTimeoutCts = new CancellationTokenSource(opts.ToolTimeout);
                    using var stageLinked = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        stageTimeoutCts.Token);
                    var stageResult = await runner
                        .RunAsync("gdal_translate", stageArgs, workspace, stageLinked.Token)
                        .ConfigureAwait(false);
                    if (!stageResult.Succeeded || !File.Exists(inputPath))
                    {
                        return JobExecutionResult.Failed(
                            $"Raster source '{roles[i]}' failed its pinned worker read: "
                            + GdalErrorSanitizer.Sanitize(stageResult.StandardError, workspace));
                    }
                }
                if (i == 0)
                {
                    firstInputPath = inputPath;
                }
                args.Add($"-{letter}");
                args.Add(inputPath);
            }

            args.Add("--calc");
            args.Add(calc);
            args.Add("--type");
            args.Add("Float32");

            // Propagate NoData: explicit override, else the first band-role raster's
            // band NoData (best-effort). --hideNoData is never used (it would DISABLE
            // gdal_calc's per-input masking).
            var effectiveNoData = explicitNoData
                ?? await GdalNoData.TryReadSourceNoDataAsync(
                    runner, firstInputPath, workspace, opts.ToolTimeout, cancellationToken).ConfigureAwait(false);
            GdalNoData.AppendNoDataArg(args, effectiveNoData);

            args.Add("--overwrite");
            args.Add("--quiet");
            args.Add("--outfile");
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, $"Running gdal_calc {index}", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_calc.py", args, workspace, outputPath,
                GeoTiffContentType, "Spectral-index raster",
                "Encoding spectral-index raster artifact", "Spectral index completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Maps the requested index onto its ordered band roles (bound to A, B, C…) and
    /// the TRUSTED calc expression. A <c>1.0*</c> coefficient forces float division
    /// so integer surface-reflectance inputs do not truncate the ratio. SAVI reads an
    /// optional soil-adjustment factor <c>L</c> (default 0.5), substituted as a
    /// validated numeric literal.
    /// </summary>
    private static bool TryBuildIndex(
        string index,
        IReadOnlyDictionary<string, string> parameters,
        out IReadOnlyList<string> roles,
        out string calc,
        out string failure)
    {
        roles = [];
        calc = "";
        failure = "";

        switch (index)
        {
            case "ndvi":
                roles = ["nir", "red"];
                calc = "(1.0*A-B)/(1.0*A+B)";
                return true;
            case "ndwi":
                roles = ["green", "nir"];
                calc = "(1.0*A-B)/(1.0*A+B)";
                return true;
            case "ndbi":
                roles = ["swir", "nir"];
                calc = "(1.0*A-B)/(1.0*A+B)";
                return true;
            case "savi":
                {
                    if (!TryReadL(parameters, out var l, out failure))
                    {
                        return false;
                    }
                    var lLiteral = l.ToString("R", CultureInfo.InvariantCulture);
                    roles = ["nir", "red"];
                    calc = $"((1.0*A-B)/(1.0*A+B+{lLiteral}))*(1.0+{lLiteral})";
                    return true;
                }
            case "evi":
                roles = ["nir", "red", "blue"];
                calc = "2.5*((1.0*A-B)/(1.0*A+6.0*B-7.5*C+1.0))";
                return true;
            default:
                failure = $"unsupported index '{index}'";
                return false;
        }
    }

    private static bool TryReadL(IReadOnlyDictionary<string, string> parameters, out double l, out string failure)
    {
        l = 0.5;
        failure = "";
        if (!GdalJobInputReader.TryGetInput(parameters, "L", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed < 0d || parsed > 1d)
        {
            failure = $"SAVI soil factor 'L' must be in the closed range [0, 1]; got '{raw}'";
            return false;
        }

        l = parsed;
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9350, LogLevel.Warning,
            "GDAL spectral-index executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9351, LogLevel.Warning,
            "GDAL spectral-index executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
