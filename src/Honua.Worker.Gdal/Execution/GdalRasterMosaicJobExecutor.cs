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
/// Native-profile <see cref="IProcessExecutor"/> for the <c>raster.mosaic</c>
/// process: combines multiple input rasters into one, backed by the GDAL
/// <c>gdalwarp</c> CLI (which merges several sources into a single output grid).
///
/// <para>
/// Inputs are supplied inline on the <c>sources</c> parameter as a
/// <c>|</c>-separated list of base64-encoded GeoTIFFs (two or more). The
/// <c>operator</c> parameter selects overlap behavior: <c>last</c> (default,
/// later-listed source wins) or <c>first</c> (earlier-listed source wins,
/// implemented by reversing source order). Statistical operators (min/max/mean/
/// sum/blend) are not yet expressible through stock gdalwarp and are rejected with
/// a clear message rather than silently approximated.
/// </para>
/// Runs only inside the GDAL worker image — <see cref="AcceptedRuntimeProfiles"/>
/// is <c>{ "native" }</c>.
/// </summary>
internal sealed partial class GdalRasterMosaicJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterMosaicJobExecutor> logger) : IProcessExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.mosaic";

    /// <summary>Separator between base64 raster entries in the <c>sources</c> input.</summary>
    public const string SourceSeparator = "|";

    /// <summary>
    /// Upper bound on the number of mosaic sources. gdalwarp can merge many tiles, but
    /// the worker decodes them all into memory and writes them to the scratch workspace
    /// first, so the count is bounded to keep a single job from exhausting the host.
    /// </summary>
    public const int MaxSources = 256;

    private const string GeoTiffContentType = "image/tiff; application=geotiff";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Mirrors ProcessPlanValidator.RasterResamplingValues so executor + validator
    // agree on the accepted set.
    private static readonly FrozenDictionary<string, string> ResamplingMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nearestneighbor"] = "near",
            ["nearest-neighbor"] = "near",
            ["nearest"] = "near",
            ["bilinear"] = "bilinear",
            ["cubic"] = "cubic",
            ["bicubic"] = "cubic",
            ["lanczos"] = "lanczos",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    // Overlap operators expressible through stock gdalwarp source ordering.
    private static readonly FrozenSet<string> SupportedOperators = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "first",
        "last",
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
        await context.ReportProgressAsync(5, "Parsing mosaic inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        var mosaicOperator = "last";
        if (GdalJobInputReader.TryGetInput(parameters, "operator", out var operatorRaw)
            && !string.IsNullOrWhiteSpace(operatorRaw))
        {
            mosaicOperator = operatorRaw.Trim();
            if (!SupportedOperators.Contains(mosaicOperator))
            {
                return JobExecutionResult.Failed(
                    $"Invalid mosaic inputs: 'operator' value '{operatorRaw}' is not supported by the native worker " +
                    "(supported operators: first, last). Statistical operators (min, max, mean, sum) are not yet available.");
            }
        }

        // Mosaic defaults to nearest-neighbor (gdalwarp's default) so aligned grids
        // merge without resampling artifacts.
        var resamplingFlag = "near";
        if (GdalJobInputReader.TryGetInput(parameters, "resampling", out var resamplingRaw)
            && !string.IsNullOrWhiteSpace(resamplingRaw)
            && !ResamplingMap.TryGetValue(resamplingRaw.Trim(), out resamplingFlag))
        {
            return JobExecutionResult.Failed(
                $"Invalid mosaic inputs: 'resampling' value '{resamplingRaw}' is not in the allowed set " +
                "(nearestneighbor, bilinear, cubic, lanczos).");
        }

        if (!TryDecodeSources(parameters, opts.MaxArtifactBytes, out var sources, out var sourcesError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourcesError);
            return JobExecutionResult.Failed($"Invalid mosaic inputs: {sourcesError}");
        }

        // Bound the DECLARED pixel footprint of every tile before any file is
        // written or gdalwarp is invoked, so a compressible GeoTIFF declaring
        // enormous dimensions cannot force a decompression-bomb allocation (#2766).
        if (!GdalRasterDimensionGuard.TryAdmitSources(sources, opts, out var dimensionError))
        {
            Log.InvalidInputs(logger, job.OperationId, dimensionError);
            return JobExecutionResult.Failed($"Invalid mosaic inputs: {dimensionError}");
        }

        // 'first' wins is achieved by reversing the source order so the desired
        // source is written LAST into the output (gdalwarp later-source-wins).
        if (string.Equals(mosaicOperator, "first", StringComparison.OrdinalIgnoreCase))
        {
            sources.Reverse();
        }

        var workspace = GdalScratch.CreateWorkspace(opts.ScratchRoot, job.OperationId);
        try
        {
            var outputPath = Path.Combine(workspace, "output.tif");
            var inputPaths = new List<string>(sources.Count);
            for (var i = 0; i < sources.Count; i++)
            {
                var inputPath = Path.Combine(workspace, $"input{i.ToString(CultureInfo.InvariantCulture)}.tif");
                await File.WriteAllBytesAsync(inputPath, sources[i], cancellationToken).ConfigureAwait(false);
                inputPaths.Add(inputPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalwarp mosaic", cancellationToken).ConfigureAwait(false);

            var args = new List<string>
            {
                "-overwrite",
                "-of", "GTiff",
                "-r", resamplingFlag,
            };
            args.AddRange(inputPaths);
            args.Add(outputPath);

            await GdalCommandLog.LogCommandAsync(context, "gdalwarp", args, workspace, cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdalwarp", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdalwarp timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, GdalErrorSanitizer.TruncateForLog(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalwarp exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            if (!File.Exists(outputPath))
            {
                return JobExecutionResult.Failed("gdalwarp reported success but produced no output raster.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Encoding mosaic raster artifact", cancellationToken).ConfigureAwait(false);

            var outputBytes = await File.ReadAllBytesAsync(outputPath, cancellationToken).ConfigureAwait(false);
            if (outputBytes.Length == 0)
            {
                return JobExecutionResult.Failed("gdalwarp produced an empty output raster.");
            }

            if (outputBytes.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, outputBytes.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Mosaic raster size {outputBytes.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(GeoTiffContentType, outputBytes);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Mosaic completed", cancellationToken).ConfigureAwait(false);

            Log.MosaicCompleted(logger, job.OperationId, sources.Count, outputBytes.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    /// <summary>
    /// Decodes the <c>|</c>-separated base64 source list, enforcing the per-entry
    /// MaxArtifactBytes ceiling and requiring at least two inputs (a mosaic of one
    /// raster is a no-op the caller almost certainly did not intend).
    /// </summary>
    private static bool TryDecodeSources(
        IReadOnlyDictionary<string, string> parameters,
        long maxBytes,
        out List<byte[]> sources,
        out string failure)
    {
        sources = [];
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, "sources", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            failure = "missing required input 'sources'";
            return false;
        }

        var entries = raw.Split(SourceSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Length < 2)
        {
            failure = $"'sources' must list at least two base64 GeoTIFF rasters separated by '{SourceSeparator}'; got {entries.Length.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        if (entries.Length > MaxSources)
        {
            failure = $"'sources' must list at most {MaxSources.ToString(CultureInfo.InvariantCulture)} rasters; got {entries.Length.ToString(CultureInfo.InvariantCulture)}";
            return false;
        }

        var decoded = new List<byte[]>(entries.Length);
        long totalBytes = 0;
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            // Conservative upper bound on the decoded size before allocating.
            if ((long)entry.Length / 4 * 3 > maxBytes)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} exceeds configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(entry);
            }
            catch (FormatException)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} is not valid base64";
                return false;
            }

            if (bytes.Length == 0)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} decoded to zero bytes";
                return false;
            }

            if (bytes.Length > maxBytes)
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} size {bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes exceeds configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            // Refuse a content-sniffed VRT/service-XML indirection blob or an embedded
            // /vsi reference before it is ever staged to scratch and opened by gdalwarp
            // (#2765). This executor decodes sources inline rather than through the
            // shared GdalJobInputReader chokepoint, so the guard must be applied here too.
            if (!GdalUntrustedInputGuard.IsAdmissible(bytes, out var guardReason))
            {
                failure = $"source #{(i + 1).ToString(CultureInfo.InvariantCulture)} rejected: {guardReason}";
                return false;
            }

            // Bound the aggregate decoded size: every source is buffered in memory and
            // staged to scratch before gdalwarp runs, so cap the total by the same
            // MaxArtifactBytes ceiling used for the merged output artifact.
            totalBytes += bytes.Length;
            if (totalBytes > maxBytes)
            {
                failure = $"decoded sources total {totalBytes.ToString(CultureInfo.InvariantCulture)} bytes, exceeding configured MaxArtifactBytes={maxBytes}";
                return false;
            }

            decoded.Add(bytes);
        }

        sources = decoded;
        return true;
    }

    private static partial class Log
    {
        [LoggerMessage(9330, LogLevel.Warning,
            "GDAL raster mosaic executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9331, LogLevel.Warning,
            "GDAL raster mosaic executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9332, LogLevel.Error,
            "GDAL raster mosaic executor failed job {OperationId}: gdalwarp exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9333, LogLevel.Error,
            "GDAL raster mosaic executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9334, LogLevel.Warning,
            "GDAL raster mosaic executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9335, LogLevel.Information,
            "GDAL raster mosaic executor completed job {OperationId}: sources={SourceCount}, bytes={Bytes}")]
        public static partial void MosaicCompleted(ILogger logger, string operationId, int sourceCount, long bytes);
    }
}
