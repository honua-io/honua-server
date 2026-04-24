// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Protocols.GeoServices.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Internal whole-raster statistics/histogram helper retained for non-public use until
/// geometry-scoped <c>computeStatisticsHistograms</c> support is implemented.
/// </summary>
internal sealed class ImageServerStatisticsHistogramsHandler
{
    /// <summary>Maximum bin count accepted from clients; matches the store-side clamp.</summary>
    private const int MaxBinCount = 1024;

    /// <summary>Default histogram bin count when the caller does not specify one.</summary>
    private const int DefaultBinCount = 256;

    private readonly ILayerCatalog _layerCatalog;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerStatisticsHistogramsHandler> _logger;

    public ImageServerStatisticsHistogramsHandler(
        ILayerCatalog layerCatalog,
        IRasterStore rasterStore,
        ILogger<ImageServerStatisticsHistogramsHandler> logger)
    {
        _layerCatalog = layerCatalog ?? throw new ArgumentNullException(nameof(layerCatalog));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Computes statistics and histograms for the layer's primary raster.
    /// </summary>
    public async Task<IResult> ComputeAsync(
        HttpContext context,
        int layerId,
        IReadOnlyDictionary<string, StringValues> values,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "compute-statistics-histograms",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "compute-statistics-histograms");

        try
        {
            var layer = await _layerCatalog.GetLayerAsync(layerId, cancellationToken);
            if (layer is null)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            if (!IsSupportedFormat(GetString(values, "f")))
            {
                ImageServerLog.InvalidStatisticsHistogramsParameters(_logger, layerId, "Unsupported format");
                return StandardErrorHelpers.CreateBadRequest(
                    context,
                    "Only JSON format is supported. Use f=json or f=pjson.");
            }

            if (!TryParseBinCount(GetString(values, "histogramParameters"), out var binCount, out var binError))
            {
                ImageServerLog.InvalidStatisticsHistogramsParameters(_logger, layerId, binError ?? "Invalid histogramParameters");
                return StandardErrorHelpers.CreateBadRequest(context, binError ?? "Invalid histogramParameters.");
            }

            // Per ArcGIS ImageServer spec, rasterIds selects raster catalog object IDs (long).
            // Band selection (1-based) is done via the optional bandIds extension parameter.
            if (!TryParseRasterIds(GetString(values, "rasterIds"), out var rasterIds, out var rasterIdsError))
            {
                ImageServerLog.InvalidStatisticsHistogramsParameters(_logger, layerId, rasterIdsError ?? "Invalid rasterIds");
                return StandardErrorHelpers.CreateBadRequest(context, rasterIdsError ?? "Invalid rasterIds.");
            }

            if (!TryParseBandIds(GetString(values, "bandIds"), out var bands, out var bandIdsError))
            {
                ImageServerLog.InvalidStatisticsHistogramsParameters(_logger, layerId, bandIdsError ?? "Invalid bandIds");
                return StandardErrorHelpers.CreateBadRequest(context, bandIdsError ?? "Invalid bandIds.");
            }

            // Resolve the catalog rasters to analyse. When rasterIds is omitted we fall back to the
            // layer's primary raster, matching ArcGIS Server's default behaviour.
            var targetRasters = new List<RasterInfo>();
            if (rasterIds is { Length: > 0 })
            {
                foreach (var id in rasterIds)
                {
                    var info = await _rasterStore.GetRasterInfoAsync(layerId, id, cancellationToken);
                    if (info is null)
                    {
                        ImageServerLog.InvalidStatisticsHistogramsParameters(
                            _logger,
                            layerId,
                            $"rasterId {id} not found");
                        return StandardErrorHelpers.CreateBadRequest(
                            context,
                            $"rasterId '{id}' not found in layer.");
                    }

                    targetRasters.Add(info.Value);
                }
            }
            else
            {
                var primaryRaster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
                if (primaryRaster is null)
                {
                    ImageServerLog.NoRastersFound(_logger, layerId);
                    return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
                }

                targetRasters.Add(primaryRaster.Value);
            }

            var statisticsList = new List<BandStatistic>();
            var histogramsList = new List<BandHistogram>();
            foreach (var raster in targetRasters)
            {
                var statistics = await _rasterStore.GetStatisticsAsync(layerId, raster.Id, bands, cancellationToken);
                var histograms = await _rasterStore.GetHistogramsAsync(layerId, raster.Id, bands, binCount, cancellationToken);

                // The store implementations do not guarantee a shared band order:
                // GetStatisticsAsync streams cached rows in DB ascending order while
                // GetHistogramsAsync preserves the caller-supplied band order. Index both
                // sets by band number and emit them in a single canonical order so the
                // parallel statistics/histograms arrays in the response stay aligned.
                AppendAlignedBandResults(statisticsList, histogramsList, bands, statistics, histograms);
            }

            var response = new ComputeStatisticsHistogramsResponse
            {
                Statistics = statisticsList.ToArray(),
                Histograms = histogramsList.ToArray(),
            };

            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, statisticsList.Count);
            scope.SetSuccess(statisticsList.Count);

            return Results.Json(response, ImageServerJsonContext.Default.ComputeStatisticsHistogramsResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.StatisticsHistogramsFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while computing statistics and histograms.");
        }
    }

    /// <summary>
    /// Appends band statistics and histograms in a canonical, mutually-aligned order so the
    /// response's parallel arrays describe the same band at each index. Order preference:
    /// (1) the caller-supplied band list, (2) the statistics order, then any remaining
    /// histogram-only bands. Bands missing from one side are zero-filled on that side.
    /// </summary>
    private static void AppendAlignedBandResults(
        List<BandStatistic> statisticsList,
        List<BandHistogram> histogramsList,
        int[]? requestedBands,
        RasterStatistics[] statistics,
        RasterHistogram[] histograms)
    {
        var statsByBand = new Dictionary<int, RasterStatistics>(statistics.Length);
        foreach (var s in statistics)
        {
            statsByBand[s.Band] = s;
        }

        var histByBand = new Dictionary<int, RasterHistogram>(histograms.Length);
        foreach (var h in histograms)
        {
            histByBand[h.Band] = h;
        }

        var orderedBands = new List<int>(Math.Max(requestedBands?.Length ?? 0, statistics.Length + histograms.Length));
        var seen = new HashSet<int>();

        if (requestedBands is { Length: > 0 })
        {
            foreach (var band in requestedBands)
            {
                if (seen.Add(band))
                {
                    orderedBands.Add(band);
                }
            }
        }

        foreach (var s in statistics)
        {
            if (seen.Add(s.Band))
            {
                orderedBands.Add(s.Band);
            }
        }

        foreach (var h in histograms)
        {
            if (seen.Add(h.Band))
            {
                orderedBands.Add(h.Band);
            }
        }

        foreach (var band in orderedBands)
        {
            // Drop bands the store filtered out from both sides (e.g. caller asked for a band
            // the raster does not have on either pipeline). When a band is present on only one
            // side we still emit it and zero-fill the missing side below, so the parallel
            // statistics[]/histograms[] arrays stay index-aligned.
            var hasStats = statsByBand.TryGetValue(band, out var s);
            var hasHist = histByBand.TryGetValue(band, out var h);
            if (!hasStats && !hasHist)
            {
                continue;
            }

            statisticsList.Add(hasStats
                ? new BandStatistic
                {
                    Min = s.MinValue ?? 0,
                    Max = s.MaxValue ?? 0,
                    Mean = s.MeanValue ?? 0,
                    StandardDeviation = s.StandardDeviation ?? 0,
                    Count = s.ValidPixelCount,
                }
                : new BandStatistic());

            histogramsList.Add(hasHist
                ? new BandHistogram
                {
                    Size = h.BinCount,
                    Min = h.Min,
                    Max = h.Max,
                    Counts = h.Counts,
                }
                : new BandHistogram { Counts = Array.Empty<long>() });
        }
    }

    private static bool TryParseBinCount(string? raw, out int binCount, out string? error)
    {
        binCount = DefaultBinCount;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        // histogramParameters is a JSON document like {"size": 64} per Esri spec.
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(raw);
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                error = "histogramParameters must be a JSON object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("size", out var sizeElement))
            {
                return true;
            }

            if (sizeElement.ValueKind == System.Text.Json.JsonValueKind.Number &&
                sizeElement.TryGetInt32(out var size))
            {
                if (size <= 0)
                {
                    error = "histogramParameters.size must be a positive integer.";
                    return false;
                }
                binCount = Math.Min(size, MaxBinCount);
                return true;
            }

            error = "histogramParameters.size must be a positive integer.";
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            error = "histogramParameters must be valid JSON.";
            return false;
        }
    }

    /// <summary>
    /// Parses the Esri <c>rasterIds</c> parameter as a list of raster catalog object IDs (long).
    /// Accepts CSV (e.g. <c>1,3</c>) or JSON array (e.g. <c>[1,3]</c>) form.
    /// </summary>
    private static bool TryParseRasterIds(string? raw, out long[]? rasterIds, out string? error)
    {
        rasterIds = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    error = "rasterIds must be a JSON array of catalog object IDs.";
                    return false;
                }

                var values = new List<long>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt64(out var id) || id <= 0)
                    {
                        error = "rasterIds entries must be positive catalog object IDs.";
                        return false;
                    }
                    values.Add(id);
                }
                rasterIds = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "rasterIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<long>(parts.Length);
        foreach (var part in parts)
        {
            if (!long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
            {
                error = "rasterIds entries must be positive catalog object IDs.";
                return false;
            }
            parsed.Add(id);
        }
        rasterIds = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }

    /// <summary>
    /// Parses the optional <c>bandIds</c> extension parameter as a list of 1-based band indices.
    /// Accepts CSV or JSON array form.
    /// </summary>
    private static bool TryParseBandIds(string? raw, out int[]? bands, out string? error)
    {
        bands = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    error = "bandIds must be a JSON array of 1-based band indices.";
                    return false;
                }

                var values = new List<int>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt32(out var band) || band <= 0)
                    {
                        error = "bandIds entries must be positive integers.";
                        return false;
                    }
                    values.Add(band);
                }
                bands = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "bandIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band) || band <= 0)
            {
                error = "bandIds entries must be positive integers.";
                return false;
            }
            parsed.Add(band);
        }
        bands = parsed.Count == 0 ? null : parsed.ToArray();
        return true;
    }

    private static bool IsSupportedFormat(string? format)
        => string.IsNullOrWhiteSpace(format) ||
           string.Equals(format, "json", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(IReadOnlyDictionary<string, StringValues> values, string key)
        => values.TryGetValue(key, out var raw) ? raw.ToString() : null;
}
