// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the catalog
/// <c>raster.statistics</c> and <c>raster.histogram</c> processes, backed by
/// <c>gdalinfo -json -stats</c> / <c>-hist</c>. One executor handles both ids
/// because gdalinfo emits a single JSON document covering stats and histograms
/// in one invocation; the per-id difference is what we extract from that
/// document and publish back as a scalar artifact.
/// </summary>
public sealed partial class GdalRasterStatisticsJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterStatisticsJobExecutor> logger) : IJobExecutor
{
    /// <summary>Process id for per-band statistics.</summary>
    public const string StatisticsProcessId = "raster.statistics";

    /// <summary>Process id for per-band histograms.</summary>
    public const string HistogramProcessId = "raster.histogram";

    /// <summary>The set of process ids this executor routes.</summary>
    public static readonly IReadOnlySet<string> SupportedProcessIds =
        new HashSet<string>(StringComparer.Ordinal) { StatisticsProcessId, HistogramProcessId };

    private const string ScalarContentType = "application/json";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

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
        if (processId is null || !SupportedProcessIds.Contains(processId))
        {
            Log.UnsupportedProcessId(logger, job.OperationId, processId ?? "<none>");
            return JobExecutionResult.Failed(
                $"Process id '{processId ?? "<none>"}' is not handled by the raster.statistics/histogram executor.");
        }

        var isHistogram = string.Equals(processId, HistogramProcessId, StringComparison.Ordinal);

        cancellationToken.ThrowIfCancellationRequested();
        await context.ReportProgressAsync(5, $"Parsing {processId} inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid {processId} inputs: {sourceError}");
        }

        if (sourceBytes.Length > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed(
                $"Source raster {sourceBytes.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
        }

        if (!TryParseBands(parameters, out var bandFilter, out var bandError))
        {
            return JobExecutionResult.Failed($"Invalid {processId} inputs: {bandError}");
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(40, "Running gdalinfo", cancellationToken).ConfigureAwait(false);

            var args = new List<string> { "-json", "-stats" };
            if (isHistogram)
            {
                args.Add("-hist");
            }
            args.Add(inputPath);

            using var timeoutCts = new CancellationTokenSource(opts.ToolTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            GdalCommandResult result;
            try
            {
                result = await runner.RunAsync("gdalinfo", args, workspace, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                return JobExecutionResult.Failed($"gdalinfo timed out after {opts.ToolTimeout}.");
            }

            if (!result.Succeeded)
            {
                Log.ToolFailed(logger, job.OperationId, result.ExitCode, Truncate(result.StandardError));
                return JobExecutionResult.Failed(
                    $"gdalinfo exited with code {result.ExitCode}: {GdalErrorSanitizer.Sanitize(result.StandardError, workspace)}");
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(80, "Projecting scalar artifact", cancellationToken).ConfigureAwait(false);

            string scalar;
            try
            {
                scalar = isHistogram
                    ? ProjectHistogramScalar(result.StandardOutput, bandFilter)
                    : ProjectStatisticsScalar(result.StandardOutput, bandFilter);
            }
            catch (JsonException ex)
            {
                return JobExecutionResult.Failed(
                    $"gdalinfo returned output that could not be parsed as JSON: {ex.Message}");
            }
            catch (InvalidOperationException ex)
            {
                return JobExecutionResult.Failed(ex.Message);
            }

            var payload = Encoding.UTF8.GetBytes(scalar);
            if (payload.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, payload.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Scalar artifact size {payload.Length} bytes exceeds configured " +
                    $"MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(ScalarContentType, payload);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, $"{processId} completed", cancellationToken).ConfigureAwait(false);

            Log.OperationCompleted(logger, job.OperationId, processId, payload.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static bool TryParseBands(
        IReadOnlyDictionary<string, string> parameters,
        out HashSet<int>? bands,
        out string failure)
    {
        bands = null;
        failure = "";

        if (!GdalJobInputReader.TryGetInput(parameters, "bands", out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new HashSet<int>();
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 1)
            {
                failure = $"'bands' must be a comma-separated list of 1-based positive integers; got '{raw}'";
                return false;
            }
            parsed.Add(value);
        }

        bands = parsed;
        return true;
    }

    /// <summary>
    /// Extracts {min, max, mean, stddev, validCount} per band from the gdalinfo
    /// JSON document. Filters to the requested band list when one was supplied.
    /// </summary>
    internal static string ProjectStatisticsScalar(string gdalinfoJson, HashSet<int>? bandFilter)
    {
        using var document = JsonDocument.Parse(gdalinfoJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("bands", out var bandsElement) || bandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("gdalinfo output did not include a 'bands' array.");
        }

        var writerOptions = new JsonWriterOptions { Indented = false };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "raster.statistics");
            writer.WriteStartArray("bands");
            foreach (var band in bandsElement.EnumerateArray())
            {
                if (!band.TryGetProperty("band", out var bandIndexElement)
                    || bandIndexElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                var bandIndex = bandIndexElement.GetInt32();
                if (bandFilter is not null && !bandFilter.Contains(bandIndex))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteNumber("band", bandIndex);
                if (band.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
                {
                    writer.WriteString("type", typeElement.GetString());
                }
                CopyNumberProperty(band, "minimum", "min", writer);
                CopyNumberProperty(band, "maximum", "max", writer);
                CopyNumberProperty(band, "mean", "mean", writer);
                CopyNumberProperty(band, "stdDev", "stddev", writer);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Extracts the histogram block per band from the gdalinfo JSON document
    /// (gdalinfo embeds <c>histogram</c> inside each band when <c>-hist</c> is
    /// passed). Filters to the requested band list when one was supplied. The
    /// bucket count is fixed by gdalinfo (the CLI has no rebucketing switch)
    /// and recorded on the artifact alongside the buckets so downstream
    /// callers know not to assume a configurable bucketing.
    /// </summary>
    internal static string ProjectHistogramScalar(string gdalinfoJson, HashSet<int>? bandFilter)
    {
        using var document = JsonDocument.Parse(gdalinfoJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("bands", out var bandsElement) || bandsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("gdalinfo output did not include a 'bands' array.");
        }

        var writerOptions = new JsonWriterOptions { Indented = false };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "raster.histogram");
            // gdalinfo -hist is fixed-bucket and exposes no rebucketing switch;
            // record the actual bucket count so callers do not assume the
            // catalog can drive bin granularity.
            writer.WriteString("bucketSource", "gdalinfo -hist (fixed bucketing)");
            writer.WriteStartArray("bands");
            foreach (var band in bandsElement.EnumerateArray())
            {
                if (!band.TryGetProperty("band", out var bandIndexElement)
                    || bandIndexElement.ValueKind != JsonValueKind.Number)
                {
                    continue;
                }
                var bandIndex = bandIndexElement.GetInt32();
                if (bandFilter is not null && !bandFilter.Contains(bandIndex))
                {
                    continue;
                }

                writer.WriteStartObject();
                writer.WriteNumber("band", bandIndex);
                if (band.TryGetProperty("histogram", out var histogramElement)
                    && histogramElement.ValueKind == JsonValueKind.Object)
                {
                    CopyNumberProperty(histogramElement, "min", "min", writer);
                    CopyNumberProperty(histogramElement, "max", "max", writer);
                    if (histogramElement.TryGetProperty("buckets", out var bucketsElement)
                        && bucketsElement.ValueKind == JsonValueKind.Array)
                    {
                        writer.WritePropertyName("buckets");
                        bucketsElement.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void CopyNumberProperty(JsonElement source, string sourceName, string targetName, Utf8JsonWriter writer)
    {
        if (!source.TryGetProperty(sourceName, out var element) || element.ValueKind != JsonValueKind.Number)
        {
            return;
        }

        if (element.TryGetDouble(out var d))
        {
            writer.WriteNumber(targetName, d);
        }
    }

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private static partial class Log
    {
        [LoggerMessage(9280, LogLevel.Warning,
            "GDAL raster statistics executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9281, LogLevel.Warning,
            "GDAL raster statistics executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9282, LogLevel.Error,
            "GDAL raster statistics executor failed job {OperationId}: gdalinfo exit {ExitCode}: {Error}")]
        public static partial void ToolFailed(ILogger logger, string operationId, int exitCode, string error);

        [LoggerMessage(9283, LogLevel.Error,
            "GDAL raster statistics executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9284, LogLevel.Warning,
            "GDAL raster statistics executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9285, LogLevel.Information,
            "GDAL raster statistics executor completed job {OperationId}: process={ProcessId}, bytes={Bytes}")]
        public static partial void OperationCompleted(ILogger logger, string operationId, string processId, long bytes);
    }
}
