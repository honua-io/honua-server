// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IProcessExecutor"/> for the <c>raster.reclassify</c>
/// process: remaps source pixel values per a supplied remap table, backed by the
/// GDAL <c>gdal_calc.py</c> CLI (#2239). This closes the reclassify gap the #2141
/// slice explicitly deferred.
///
/// <para>
/// The <c>remap</c> table is a <c>;</c>-separated list of entries, each
/// <c>key:value</c> where the key is either a single number (<c>v</c>) or a half-open
/// range (<c>lo..hi</c>, lower-inclusive / upper-exclusive) and the value is the
/// output number. Unmatched pixels take the optional <c>defaultValue</c>, or the
/// original value when omitted. Every token is parsed as a number BEFORE it is
/// folded into the trusted nested-<c>where</c> calc expression, so no caller text
/// reaches the <c>eval()</c>'d calc string.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/> is
/// <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterReclassifyJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterReclassifyJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.reclassify";

    /// <summary>Separator between remap entries.</summary>
    public const string EntrySeparator = ";";

    /// <summary>Separator between the lower and upper bound of a range key.</summary>
    public const string RangeSeparator = "..";

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
        await context.ReportProgressAsync(5, "Parsing reclassify inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetInput(parameters, "remap", out var remap) || string.IsNullOrWhiteSpace(remap))
        {
            return JobExecutionResult.Failed("Invalid reclassify inputs: missing required input 'remap'.");
        }

        if (!TryBuildCalc(remap, parameters, out var calc, out var remapError))
        {
            Log.InvalidInputs(logger, job.OperationId, remapError);
            return JobExecutionResult.Failed($"Invalid reclassify inputs: {remapError}");
        }

        string? dataType = null;
        if (GdalJobInputReader.TryGetInput(parameters, "dataType", out var dataTypeRaw)
            && !string.IsNullOrWhiteSpace(dataTypeRaw))
        {
            if (!GdalCalcInputs.TryNormalizeDataType(dataTypeRaw, out dataType, out var dataTypeError))
            {
                return JobExecutionResult.Failed($"Invalid reclassify inputs: {dataTypeError}");
            }
        }

        if (!GdalNoData.TryReadExplicitNoData(parameters, out var explicitNoData, out var noDataError))
        {
            return JobExecutionResult.Failed($"Invalid reclassify inputs: {noDataError}");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", opts.MaxArtifactBytes, out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid reclassify inputs: {sourceError}");
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            var outputPath = Path.Combine(workspace, "output.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-A", inputPath,
                "--calc", calc,
            };
            if (dataType is not null)
            {
                args.Add("--type");
                args.Add(dataType);
            }

            // Propagate NoData: explicit override, else the source's band NoData
            // (best-effort). --hideNoData is never used (it would DISABLE masking).
            var effectiveNoData = explicitNoData
                ?? await GdalNoData.TryReadSourceNoDataAsync(
                    runner, inputPath, workspace, opts.ToolTimeout, cancellationToken).ConfigureAwait(false);
            GdalNoData.AppendNoDataArg(args, effectiveNoData);

            args.Add("--overwrite");
            args.Add("--quiet");
            args.Add("--outfile");
            args.Add(outputPath);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdal_calc reclassify", cancellationToken).ConfigureAwait(false);

            return await GdalToolExecution.RunAndPublishAsync(
                runner, context, opts, logger, job.OperationId,
                "gdal_calc.py", args, workspace, outputPath,
                GeoTiffContentType, "Reclassified raster",
                "Encoding reclassified raster artifact", "Reclassify completed",
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Parses the remap table and folds it into a trusted nested-<c>where</c> NumPy
    /// expression. Every bound and output value is parsed as a finite number, so the
    /// resulting calc string contains only digits, the band variable <c>A</c>, and a
    /// fixed operator/function vocabulary — no caller text survives parsing.
    /// </summary>
    public static bool TryBuildCalc(
        string remap,
        IReadOnlyDictionary<string, string> parameters,
        out string calc,
        out string failure)
    {
        calc = "";
        failure = "";

        var defaultExpr = "A";
        if (GdalJobInputReader.TryGetInput(parameters, "defaultValue", out var defaultRaw)
            && !string.IsNullOrWhiteSpace(defaultRaw))
        {
            if (!TryParseNumber(defaultRaw, out var defaultValue))
            {
                failure = $"'defaultValue' must be a finite number; got '{defaultRaw}'";
                return false;
            }
            defaultExpr = Literal(defaultValue);
        }

        var entries = remap.Split(EntrySeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length == 0)
        {
            failure = $"'remap' must list at least one 'key:value' entry separated by '{EntrySeparator}'";
            return false;
        }

        var conditions = new List<(string Condition, string Output)>(entries.Length);
        foreach (var entry in entries)
        {
            var colon = entry.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || colon == entry.Length - 1)
            {
                failure = $"remap entry '{entry}' must have the form 'value:newValue' or 'lo..hi:newValue'";
                return false;
            }

            var keyPart = entry[..colon].Trim();
            var valuePart = entry[(colon + 1)..].Trim();

            if (!TryParseNumber(valuePart, out var output))
            {
                failure = $"remap entry '{entry}' has a non-numeric output value '{valuePart}'";
                return false;
            }

            string condition;
            var rangeIndex = keyPart.IndexOf(RangeSeparator, StringComparison.Ordinal);
            if (rangeIndex >= 0)
            {
                var loRaw = keyPart[..rangeIndex].Trim();
                var hiRaw = keyPart[(rangeIndex + RangeSeparator.Length)..].Trim();
                if (!TryParseNumber(loRaw, out var lo) || !TryParseNumber(hiRaw, out var hi))
                {
                    failure = $"remap entry '{entry}' has a non-numeric range bound";
                    return false;
                }
                if (hi <= lo)
                {
                    failure = $"remap entry '{entry}' must have lo < hi";
                    return false;
                }
                condition = $"(A>={Literal(lo)})&(A<{Literal(hi)})";
            }
            else
            {
                if (!TryParseNumber(keyPart, out var value))
                {
                    failure = $"remap entry '{entry}' has a non-numeric key '{keyPart}'";
                    return false;
                }
                condition = $"(A=={Literal(value)})";
            }

            conditions.Add((condition, Literal(output)));
        }

        // Fold right-to-left into nested where(cond, output, <rest>).
        var builder = new StringBuilder(defaultExpr);
        for (var i = conditions.Count - 1; i >= 0; i--)
        {
            var (condition, output) = conditions[i];
            builder.Insert(0, $"where({condition},{output},");
            builder.Append(')');
        }

        calc = builder.ToString();
        return true;
    }

    private static bool TryParseNumber(string raw, out double value)
        => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
            && !double.IsNaN(value) && !double.IsInfinity(value);

    private static string Literal(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static partial class Log
    {
        [LoggerMessage(9360, LogLevel.Warning,
            "GDAL reclassify executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9361, LogLevel.Warning,
            "GDAL reclassify executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);
    }
}
