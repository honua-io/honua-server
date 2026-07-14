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
/// Native-profile <see cref="IProcessExecutor"/> for the <c>raster.map-algebra</c>
/// process: evaluates an ALLOW-LISTED arithmetic/logical expression across one or
/// more input rasters, backed by the GDAL <c>gdal_calc.py</c> CLI (#2239).
///
/// <para>
/// Inputs are supplied inline on the <c>sources</c> parameter as a <c>|</c>-separated
/// list of base64-encoded GeoTIFFs (1–26). Each source is bound to a single-letter
/// band variable A, B, C, … in list order. The <c>expression</c> is validated against
/// a strict allow-list (single-uppercase band variables, numeric literals, a fixed set
/// of arithmetic/logical operators, and a small allow-list of safe NumPy functions)
/// BEFORE it ever reaches the CLI — <c>gdal_calc.py</c> evaluates the calc string with
/// <c>eval()</c>, so an un-vetted expression would be remote code execution. The
/// allow-list never shell-evaluates arbitrary input.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterMapAlgebraJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterMapAlgebraJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.map-algebra";

    /// <summary>Separator between base64 raster entries in the <c>sources</c> input.</summary>
    public const string SourceSeparator = "|";

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
        await context.ReportProgressAsync(5, "Parsing map-algebra inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalCalcInputs.TryDecodeSources(parameters, opts.MaxArtifactBytes, out var sources, out var sourcesError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourcesError);
            return JobExecutionResult.Failed($"Invalid map-algebra inputs: {sourcesError}");
        }

        // Bound the DECLARED pixel footprint before any file is written or GDAL is
        // invoked, so a compressible GeoTIFF declaring enormous dimensions cannot
        // force a decompression-bomb allocation (#2766).
        if (!GdalRasterDimensionGuard.TryAdmitSources(sources, opts, out var dimensionError))
        {
            Log.InvalidInputs(logger, job.OperationId, dimensionError);
            return JobExecutionResult.Failed($"Invalid map-algebra inputs: {dimensionError}");
        }

        if (!GdalJobInputReader.TryGetInput(parameters, "expression", out var expression)
            || string.IsNullOrWhiteSpace(expression))
        {
            return JobExecutionResult.Failed("Invalid map-algebra inputs: missing required input 'expression'.");
        }

        if (!MapAlgebraExpression.IsAllowed(expression, sources.Count, out var expressionError))
        {
            Log.InvalidInputs(logger, job.OperationId, expressionError);
            return JobExecutionResult.Failed($"Invalid map-algebra inputs: {expressionError}");
        }

        string? dataType = null;
        if (GdalJobInputReader.TryGetInput(parameters, "dataType", out var dataTypeRaw)
            && !string.IsNullOrWhiteSpace(dataTypeRaw)
            && !GdalCalcInputs.TryNormalizeDataType(dataTypeRaw, out dataType, out var dataTypeError))
        {
            return JobExecutionResult.Failed($"Invalid map-algebra inputs: {dataTypeError}");
        }

        if (!GdalNoData.TryReadExplicitNoData(parameters, out var explicitNoData, out var noDataError))
        {
            return JobExecutionResult.Failed($"Invalid map-algebra inputs: {noDataError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            // Second segment is a fixed relative literal filename, so it can never be
            // rooted and silently discard workspace.
            var outputPath = Path.Combine(workspace, "output.tif");
            var firstInputPath = "";
            var args = new List<string>(sources.Count * 2 + 8);
            for (var i = 0; i < sources.Count; i++)
            {
                var letter = (char)('A' + i);
                // Second segment is a generated relative literal (a loop counter, never
                // user input), so it can never be rooted and silently discard workspace.
                var inputPath = Path.Combine(workspace, $"input{i.ToString(CultureInfo.InvariantCulture)}.tif");
                await File.WriteAllBytesAsync(inputPath, sources[i], cancellationToken).ConfigureAwait(false);
                if (i == 0)
                {
                    firstInputPath = inputPath;
                }
                args.Add($"-{letter}");
                args.Add(inputPath);
            }

            // Pass the calc as a single "--calc=<expr>" token so an expression with a
            // leading unary minus (e.g. "-A") is not mistaken for a separate option by
            // gdal_calc.py's argparse.
            args.Add($"--calc={expression.Trim()}");
            if (dataType is not null)
            {
                args.Add("--type");
                args.Add(dataType);
            }

            // Propagate NoData: explicit override, else the first source's band NoData
            // (best-effort). Passing a real --NoDataValue tags the output band and fills
            // gdal_calc's default-masked cells with it; --hideNoData is never used.
            var effectiveNoData = explicitNoData
                ?? await GdalNoData.TryReadSourceNoDataAsync(
                    runner, firstInputPath, workspace, opts.ToolTimeout, cancellationToken).ConfigureAwait(false);
            GdalNoData.AppendNoDataArg(args, effectiveNoData);

            args.Add("--overwrite");
            args.Add("--quiet");
            args.Add("--outfile");
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_calc map algebra", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_calc.py", args, workspace, outputPath,
                GeoTiffContentType, "Map-algebra raster",
                "Encoding map-algebra raster artifact", "Map algebra completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(9340, LogLevel.Warning,
            "GDAL map-algebra executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9341, LogLevel.Warning,
            "GDAL map-algebra executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
