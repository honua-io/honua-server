// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Server.Features.ImageServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.ImageServer.Handlers;

/// <summary>
/// Handler for the Esri Image Server <c>computeStatisticsHistograms</c> endpoint.
/// Returns per-band statistics and histograms for the layer's primary raster.
/// AOI clipping is honoured by passing the supplied geometry through to the raster store
/// in a future ticket; the MVP analyses the entire raster.
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

            if (!TryParseBands(GetString(values, "rasterIds"), out var bands, out var bandsError))
            {
                ImageServerLog.InvalidStatisticsHistogramsParameters(_logger, layerId, bandsError ?? "Invalid rasterIds");
                return StandardErrorHelpers.CreateBadRequest(context, bandsError ?? "Invalid rasterIds.");
            }

            var primaryRaster = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
            if (primaryRaster is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }
            var primary = primaryRaster.Value;

            var statistics = await _rasterStore.GetStatisticsAsync(layerId, primary.Id, bands, cancellationToken);
            var histograms = await _rasterStore.GetHistogramsAsync(layerId, primary.Id, bands, binCount, cancellationToken);

            var response = new ComputeStatisticsHistogramsResponse
            {
                Statistics = statistics.Select(s => new BandStatistic
                {
                    Min = s.MinValue ?? 0,
                    Max = s.MaxValue ?? 0,
                    Mean = s.MeanValue ?? 0,
                    StandardDeviation = s.StandardDeviation ?? 0,
                    Count = s.ValidPixelCount,
                }).ToArray(),
                Histograms = histograms.Select(h => new BandHistogram
                {
                    Size = h.BinCount,
                    Min = h.Min,
                    Max = h.Max,
                    Counts = h.Counts,
                }).ToArray(),
            };

            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, statistics.Length);
            scope.SetSuccess(statistics.Length);

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

    private static bool TryParseBands(string? raw, out int[]? bands, out string? error)
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
                    error = "rasterIds must be a JSON array of band numbers.";
                    return false;
                }

                var values = new List<int>(doc.RootElement.GetArrayLength());
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    if (element.ValueKind != System.Text.Json.JsonValueKind.Number || !element.TryGetInt32(out var band) || band <= 0)
                    {
                        error = "rasterIds entries must be positive integers.";
                        return false;
                    }
                    values.Add(band);
                }
                bands = values.Count == 0 ? null : values.ToArray();
                return true;
            }
            catch (System.Text.Json.JsonException)
            {
                error = "rasterIds must be valid JSON.";
                return false;
            }
        }

        var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var band) || band <= 0)
            {
                error = "rasterIds entries must be positive integers.";
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
