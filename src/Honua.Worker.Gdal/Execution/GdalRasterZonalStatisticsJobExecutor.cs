// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Worker.Gdal.Execution;

/// <summary>
/// Native-profile <see cref="IJobExecutor"/> for the catalog
/// <c>raster.zonal-statistics</c> process. Consumes a source raster and an
/// inline GeoJSON <c>FeatureCollection</c> of zone polygons. For each zone the
/// executor runs <c>gdalwarp -cutline -crop_to_cutline</c> to extract the
/// intersecting pixels, then <c>gdalinfo -json -stats</c> on the clipped raster
/// to compute per-band aggregates. Aggregates are projected onto a single JSON
/// scalar artifact (one entry per zone, only the requested statistics emitted).
/// </summary>
public sealed partial class GdalRasterZonalStatisticsJobExecutor(
    IGdalCommandRunner runner,
    IOptionsMonitor<GdalWorkerOptions> options,
    ILogger<GdalRasterZonalStatisticsJobExecutor> logger) : IJobExecutor
{
    /// <summary>The canonical process id this executor handles.</summary>
    public const string HandledProcessId = "raster.zonal-statistics";

    private const string ScalarContentType = "application/json";

    private static readonly IReadOnlySet<string> NativeProfileSet =
        new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    // Mirrors ProcessPlanValidator.RasterZonalStatisticValues so the worker
    // refuses values the validator would have rejected.
    private static readonly FrozenSet<string> SupportedStatistics =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "count", "sum", "mean", "min", "max", "stddev", "variance"
        }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

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
        await context.ReportProgressAsync(5, "Parsing zonal statistics inputs", cancellationToken).ConfigureAwait(false);

        var opts = options.CurrentValue;

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "source", out var sourceBytes, out var sourceError))
        {
            Log.InvalidInputs(logger, job.OperationId, sourceError);
            return JobExecutionResult.Failed($"Invalid zonal statistics inputs: {sourceError}");
        }

        if (sourceBytes.Length > opts.MaxArtifactBytes)
        {
            return JobExecutionResult.Failed(
                $"Source raster {sourceBytes.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
        }

        if (!GdalJobInputReader.TryGetBase64Input(parameters, "zones", out var zonesBytes, out var zonesError))
        {
            Log.InvalidInputs(logger, job.OperationId, zonesError);
            return JobExecutionResult.Failed(
                $"Invalid zonal statistics inputs: {zonesError}. Layer-resolved zones via zonesLayerId are deferred — supply 'zones' as an inline base64 GeoJSON FeatureCollection.");
        }

        FeatureCollection zones;
        try
        {
            using var reader = new StringReader(Encoding.UTF8.GetString(zonesBytes));
            zones = new GeoJsonReader().Read<FeatureCollection>(reader.ReadToEnd())
                ?? throw new InvalidOperationException("GeoJSON parser returned null.");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return JobExecutionResult.Failed(
                $"Invalid zonal statistics inputs: 'zones' could not be parsed as a GeoJSON FeatureCollection: {ex.Message}");
        }

        if (zones.Count == 0)
        {
            return JobExecutionResult.Failed(
                "Invalid zonal statistics inputs: 'zones' contained no features.");
        }

        var band = 1;
        if (GdalJobInputReader.TryGetInput(parameters, "band", out var bandRaw))
        {
            if (!int.TryParse(bandRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out band) || band < 1)
            {
                return JobExecutionResult.Failed(
                    $"Invalid zonal statistics inputs: 'band' value '{bandRaw}' is not a positive integer.");
            }
        }

        var requestedStatistics = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "count", "mean", "stddev", "min", "max", "sum"
        };
        if (GdalJobInputReader.TryGetInput(parameters, "statistics", out var statsRaw)
            && !string.IsNullOrWhiteSpace(statsRaw))
        {
            requestedStatistics.Clear();
            foreach (var token in statsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!SupportedStatistics.Contains(token))
                {
                    return JobExecutionResult.Failed(
                        $"Invalid zonal statistics inputs: statistic '{token}' is not in the allowed set " +
                        "(count, sum, mean, min, max, stddev, variance).");
                }
                requestedStatistics.Add(token);
            }
        }

        var workspace = Path.Combine(opts.ScratchRoot, job.OperationId);
        Directory.CreateDirectory(workspace);
        try
        {
            var inputPath = Path.Combine(workspace, "input.tif");
            await File.WriteAllBytesAsync(inputPath, sourceBytes, cancellationToken).ConfigureAwait(false);

            var zonalResults = new List<ZonalRow>();
            var index = 0;
            foreach (var feature in zones)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var zoneIndex = index++;
                var geometry = feature.Geometry;
                if (geometry is null || geometry.IsEmpty)
                {
                    zonalResults.Add(ZonalRow.Skipped(zoneIndex, ZoneId(feature, zoneIndex), "empty geometry"));
                    continue;
                }
                if (geometry is not Polygon and not MultiPolygon)
                {
                    zonalResults.Add(ZonalRow.Skipped(zoneIndex, ZoneId(feature, zoneIndex),
                        $"unsupported geometry '{geometry.GeometryType}'"));
                    continue;
                }

                var cutlinePath = Path.Combine(workspace, $"cutline-{zoneIndex}.geojson");
                var clippedPath = Path.Combine(workspace, $"clipped-{zoneIndex}.tif");
                var cutlineGeoJson = GdalRasterClipJobExecutor.WriteCutlineGeoJson(geometry);
                await File.WriteAllTextAsync(cutlinePath, cutlineGeoJson, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);

                var clipArgs = new List<string>
                {
                    "-overwrite",
                    "-of", "GTiff",
                    "-cutline", cutlinePath,
                    "-crop_to_cutline",
                    "-b", band.ToString(CultureInfo.InvariantCulture),
                    inputPath,
                    clippedPath,
                };

                await context.ReportProgressAsync(
                    PercentForZone(zoneIndex, zones.Count, 10, 80),
                    $"Clipping zone {zoneIndex + 1}/{zones.Count}",
                    cancellationToken).ConfigureAwait(false);

                // ToolTimeout is documented as the per-invocation ceiling.
                // Each gdalwarp/gdalinfo call gets its own CTS so a multi-zone
                // job whose cumulative runtime exceeds the per-tool ceiling is
                // not aborted unless an individual invocation actually hangs.
                GdalCommandResult clipResult;
                using (var clipTimeoutCts = new CancellationTokenSource(opts.ToolTimeout))
                using (var clipLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, clipTimeoutCts.Token))
                {
                    try
                    {
                        clipResult = await runner.RunAsync("gdalwarp", clipArgs, workspace, clipLinked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (clipTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                        return JobExecutionResult.Failed($"gdalwarp timed out after {opts.ToolTimeout}.");
                    }
                }

                if (!clipResult.Succeeded || !File.Exists(clippedPath))
                {
                    zonalResults.Add(ZonalRow.Skipped(zoneIndex, ZoneId(feature, zoneIndex),
                        $"gdalwarp clip failed: {GdalErrorSanitizer.Sanitize(clipResult.StandardError, workspace)}"));
                    continue;
                }

                var infoArgs = new List<string> { "-json", "-stats", clippedPath };
                GdalCommandResult infoResult;
                using (var infoTimeoutCts = new CancellationTokenSource(opts.ToolTimeout))
                using (var infoLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, infoTimeoutCts.Token))
                {
                    try
                    {
                        infoResult = await runner.RunAsync("gdalinfo", infoArgs, workspace, infoLinked.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (infoTimeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        Log.ToolTimedOut(logger, job.OperationId, opts.ToolTimeout);
                        return JobExecutionResult.Failed($"gdalinfo timed out after {opts.ToolTimeout}.");
                    }
                }

                if (!infoResult.Succeeded)
                {
                    zonalResults.Add(ZonalRow.Skipped(zoneIndex, ZoneId(feature, zoneIndex),
                        $"gdalinfo failed: {GdalErrorSanitizer.Sanitize(infoResult.StandardError, workspace)}"));
                    continue;
                }

                try
                {
                    // gdalwarp -b <band> writes the requested input band as
                    // output band 1 (single-band raster); read stats from band
                    // 1 of the clipped output even though the source band can
                    // be any positive index. The original 'band' index is kept
                    // separately and emitted onto the artifact below.
                    const int ClippedOutputBand = 1;
                    var stats = ExtractZoneStatistics(infoResult.StandardOutput, ClippedOutputBand, requestedStatistics);
                    zonalResults.Add(new ZonalRow(zoneIndex, ZoneId(feature, zoneIndex), stats, SkipReason: null));
                }
                catch (JsonException ex)
                {
                    zonalResults.Add(ZonalRow.Skipped(zoneIndex, ZoneId(feature, zoneIndex),
                        $"gdalinfo JSON parse error: {ex.Message}"));
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            await context.ReportProgressAsync(90, "Projecting zonal scalar artifact", cancellationToken).ConfigureAwait(false);

            var scalar = BuildScalar(band, requestedStatistics, zonalResults);
            var payload = Encoding.UTF8.GetBytes(scalar);
            if (payload.Length > opts.MaxArtifactBytes)
            {
                Log.ArtifactTooLarge(logger, job.OperationId, payload.Length, opts.MaxArtifactBytes);
                return JobExecutionResult.Failed(
                    $"Scalar artifact size {payload.Length} bytes exceeds configured MaxArtifactBytes={opts.MaxArtifactBytes}.");
            }

            var artifactUri = GdalDataUri.Build(ScalarContentType, payload);
            await context.PublishArtifactAsync(artifactUri, cancellationToken).ConfigureAwait(false);
            await context.ReportProgressAsync(100, "Zonal statistics completed", cancellationToken).ConfigureAwait(false);

            Log.OperationCompleted(logger, job.OperationId, zonalResults.Count, payload.Length);
            return JobExecutionResult.Succeeded();
        }
        finally
        {
            GdalScratch.TryCleanup(workspace, logger);
        }
    }

    private static int PercentForZone(int zoneIndex, int zoneCount, int from, int to)
    {
        if (zoneCount <= 0)
        {
            return to;
        }
        var ratio = (double)zoneIndex / Math.Max(1, zoneCount);
        return (int)Math.Round(from + ratio * (to - from));
    }

    private static string ZoneId(IFeature feature, int fallbackIndex)
    {
        if (feature.Attributes is { } attributes)
        {
            foreach (var name in new[] { "id", "ID", "Id" })
            {
                if (attributes.Exists(name))
                {
                    var value = attributes[name];
                    if (value is not null)
                    {
                        return value.ToString() ?? fallbackIndex.ToString(CultureInfo.InvariantCulture);
                    }
                }
            }
        }
        return fallbackIndex.ToString(CultureInfo.InvariantCulture);
    }

    private static Dictionary<string, double?> ExtractZoneStatistics(
        string gdalinfoJson,
        int band,
        HashSet<string> requested)
    {
        using var document = JsonDocument.Parse(gdalinfoJson);
        var root = document.RootElement;
        if (!root.TryGetProperty("bands", out var bandsElement) || bandsElement.ValueKind != JsonValueKind.Array)
        {
            return EmptyStats(requested);
        }

        JsonElement target = default;
        foreach (var entry in bandsElement.EnumerateArray())
        {
            if (entry.TryGetProperty("band", out var bandIndex)
                && bandIndex.ValueKind == JsonValueKind.Number
                && bandIndex.GetInt32() == band)
            {
                target = entry;
                break;
            }
        }

        if (target.ValueKind == JsonValueKind.Undefined)
        {
            return EmptyStats(requested);
        }

        var stats = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        if (requested.Contains("min"))
        {
            stats["min"] = ReadNumber(target, "minimum");
        }
        if (requested.Contains("max"))
        {
            stats["max"] = ReadNumber(target, "maximum");
        }
        if (requested.Contains("mean"))
        {
            stats["mean"] = ReadNumber(target, "mean");
        }
        var stdDev = ReadNumber(target, "stdDev");
        if (requested.Contains("stddev"))
        {
            stats["stddev"] = stdDev;
        }
        if (requested.Contains("variance"))
        {
            stats["variance"] = stdDev.HasValue ? stdDev.Value * stdDev.Value : null;
        }

        // count: prefer explicit validCount when present (newer gdalinfo emits
        // it); otherwise leave as null and let downstream callers note the gap.
        double? count = null;
        if (target.TryGetProperty("validCount", out var validCountElement) && validCountElement.ValueKind == JsonValueKind.Number
            && validCountElement.TryGetDouble(out var validCount))
        {
            count = validCount;
        }
        else if (target.TryGetProperty("validPercent", out var validPercentElement)
            && validPercentElement.ValueKind == JsonValueKind.Number)
        {
            // Cannot compute count without total pixel count; leave null but
            // record the percentage so callers can reconstitute it if needed.
            stats["validPercent"] = validPercentElement.GetDouble();
        }
        if (requested.Contains("count"))
        {
            stats["count"] = count;
        }
        if (requested.Contains("sum"))
        {
            var mean = ReadNumber(target, "mean");
            stats["sum"] = (mean.HasValue && count.HasValue) ? mean.Value * count.Value : null;
        }

        return stats;
    }

    private static Dictionary<string, double?> EmptyStats(HashSet<string> requested)
    {
        var stats = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in requested)
        {
            stats[name] = null;
        }
        return stats;
    }

    private static double? ReadNumber(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetDouble(out var value))
        {
            return value;
        }
        return null;
    }

    // Emit statistics in this stable order rather than HashSet iteration order so
    // a single zonal-statistics artifact diff against a reference run remains
    // stable across runs.
    private static readonly string[] StatisticOrder =
    [
        "count", "sum", "mean", "min", "max", "stddev", "variance"
    ];

    private static string BuildScalar(int band, HashSet<string> requested, List<ZonalRow> rows)
    {
        var orderedStatistics = StatisticOrder.Where(requested.Contains).ToArray();

        var writerOptions = new JsonWriterOptions { Indented = false };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, writerOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "raster.zonal-statistics");
            writer.WriteNumber("band", band);
            writer.WriteStartArray("statistics");
            foreach (var stat in orderedStatistics)
            {
                writer.WriteStringValue(stat);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("zones");
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WriteNumber("zoneIndex", row.ZoneIndex);
                writer.WriteString("zoneId", row.ZoneId);
                if (row.SkipReason is not null)
                {
                    writer.WriteString("skipped", row.SkipReason);
                }
                else
                {
                    foreach (var stat in orderedStatistics)
                    {
                        if (row.Statistics.TryGetValue(stat, out var value) && value.HasValue)
                        {
                            writer.WriteNumber(stat, value.Value);
                        }
                        else
                        {
                            writer.WriteNull(stat);
                        }
                    }
                }
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Truncate(string value)
    {
        const int max = 500;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max] + "…";
    }

    private sealed record ZonalRow(
        int ZoneIndex,
        string ZoneId,
        IReadOnlyDictionary<string, double?> Statistics,
        string? SkipReason)
    {
        public static ZonalRow Skipped(int zoneIndex, string zoneId, string reason)
            => new(zoneIndex, zoneId, EmptyDictionary, reason);

        private static readonly IReadOnlyDictionary<string, double?> EmptyDictionary =
            new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
    }

    private static partial class Log
    {
        [LoggerMessage(9290, LogLevel.Warning,
            "GDAL zonal statistics executor refused job {OperationId}: unsupported process id '{ProcessId}'")]
        public static partial void UnsupportedProcessId(ILogger logger, string operationId, string processId);

        [LoggerMessage(9291, LogLevel.Warning,
            "GDAL zonal statistics executor rejected job {OperationId}: {Reason}")]
        public static partial void InvalidInputs(ILogger logger, string operationId, string reason);

        [LoggerMessage(9293, LogLevel.Error,
            "GDAL zonal statistics executor timed out job {OperationId} after {Timeout}")]
        public static partial void ToolTimedOut(ILogger logger, string operationId, TimeSpan timeout);

        [LoggerMessage(9294, LogLevel.Warning,
            "GDAL zonal statistics executor refused job {OperationId}: artifact size {ActualBytes} exceeds limit {MaxBytes}")]
        public static partial void ArtifactTooLarge(ILogger logger, string operationId, long actualBytes, long maxBytes);

        [LoggerMessage(9295, LogLevel.Information,
            "GDAL zonal statistics executor completed job {OperationId}: zones={ZoneCount}, bytes={Bytes}")]
        public static partial void OperationCompleted(ILogger logger, string operationId, int zoneCount, long bytes);
    }
}
