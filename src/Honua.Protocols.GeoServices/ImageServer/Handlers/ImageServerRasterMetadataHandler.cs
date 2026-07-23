// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the read-only Image Server raster metadata child resources:
/// <c>statistics</c>, <c>histograms</c>, <c>rasterAttributeTable</c>, and
/// <c>rasterFunctionInfos</c>. Each resource is a thin Esri-shaped adapter over the
/// shared raster store metadata (and, for raster functions, the shared raster-function
/// planner) so the surface stays consistent with the rest of the ImageServer pipeline.
/// </summary>
internal sealed class ImageServerRasterMetadataHandler
{
    /// <summary>Default histogram bin count, matching the store-side default.</summary>
    private const int DefaultBinCount = 256;

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly ILogger<ImageServerRasterMetadataHandler> _logger;

    public ImageServerRasterMetadataHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        ILogger<ImageServerRasterMetadataHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Returns per-band statistics for the layer's primary raster (or resolved mosaic)
    /// in the Esri <c>statistics</c> child-resource shape.
    /// </summary>
    public Task<IResult> GetStatisticsAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "statistics", async resolved =>
        {
            var statistics = await ImageServerStatisticsBudget.ResolveAsync(
                ct => ResolveStatisticsAsync(layerId, resolved, ct),
                onBudgetExceeded: () => ImageServerLog.StatisticsComputeBudgetExceeded(
                    _logger, layerId, ImageServerStatisticsBudget.Timeout.TotalSeconds),
                cancellationToken);
            var entries = new StatisticsEntry[statistics.Length];
            for (var i = 0; i < statistics.Length; i++)
            {
                var s = statistics[i];
                entries[i] = new StatisticsEntry
                {
                    Min = s.MinValue ?? 0,
                    Max = s.MaxValue ?? 0,
                    Mean = s.MeanValue ?? 0,
                    StandardDeviation = s.StandardDeviation ?? 0,
                    Count = s.ValidPixelCount,
                };
            }

            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, entries.Length);
            return (Results.Json(new StatisticsResourceResponse { Statistics = entries },
                ImageServerJsonContext.Default.StatisticsResourceResponse), entries.Length);
        });

    /// <summary>
    /// Returns per-band histograms for the layer's primary raster (or resolved mosaic)
    /// in the Esri <c>histograms</c> child-resource shape.
    /// </summary>
    public Task<IResult> GetHistogramsAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "histograms", async resolved =>
        {
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return (StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer."), 0);
            }

            RasterHistogram[] histograms;
            if (rasters.Length == 1)
            {
                histograms = await _rasterStore.GetHistogramsAsync(
                    layerId, rasters[0].Id, bands: null, DefaultBinCount, cancellationToken: cancellationToken);
            }
            else
            {
                var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
                histograms = await _rasterStore.GetMosaicHistogramsAsync(
                    layerId, rasters.Select(r => r.Id).ToArray(), mergeStrategy, bands: null, DefaultBinCount, cancellationToken: cancellationToken);
            }

            var entries = new BandHistogram[histograms.Length];
            for (var i = 0; i < histograms.Length; i++)
            {
                var h = histograms[i];
                entries[i] = new BandHistogram
                {
                    Size = h.BinCount,
                    Min = h.Min,
                    Max = h.Max,
                    Counts = h.Counts,
                };
            }

            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, entries.Length);
            return (Results.Json(new HistogramsResourceResponse { Histograms = entries },
                ImageServerJsonContext.Default.HistogramsResourceResponse), entries.Length);
        });

    /// <summary>
    /// Returns the raster functions the service can apply, in the Esri
    /// <c>rasterFunctionInfos</c> child-resource shape. The list mirrors the functions
    /// the shared raster-function planner accepts through <c>renderingRule</c>.
    /// </summary>
    public Task<IResult> GetRasterFunctionInfosAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "raster-function-infos", _ =>
        {
            // "None" is the ArcGIS-conventional identity entry advertised by every image
            // service; the remaining names are the functions the planner walks. Advertising
            // exactly the planner's supported set keeps the resource honest — clients see
            // only functions the service can actually validate/plan.
            var entries = new[]
            {
                new RasterFunctionInfoEntry
                {
                    Name = "None",
                    Description = "No raster function is applied; pixels are returned as stored.",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "Identity",
                    Description = "Passes the source raster through unchanged.",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "Stretch",
                    Description = "Applies a stretch (esriRasterStretchType) to enhance contrast.",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "Colormap",
                    Description = "Maps single-band pixel values to RGBA using explicit [value, r, g, b] stops, a named ColorrampName, or an inline algorithmic/multipart Colorramp object.",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "Clip",
                    Description = "Clips the raster to a clipping geometry or extent.",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "ExtractBand",
                    Description = "Selects and reorders output bands by 0-based band index (BandIds).",
                    Help = string.Empty,
                },
                new RasterFunctionInfoEntry
                {
                    Name = "BandArithmetic",
                    Description = "Derives an analytic band (NDVI) from two source bands selected by 0-based BandIndexes.",
                    Help = string.Empty,
                },
            };

            var result = Results.Json(
                new RasterFunctionInfosResponse { RasterFunctionInfos = entries },
                ImageServerJsonContext.Default.RasterFunctionInfosResponse);
            return Task.FromResult<(IResult, int)>((result, entries.Length));
        });

    /// <summary>
    /// Returns the raster attribute table in the Esri feature-set shape. Honua rasters are
    /// continuous (non-thematic) and carry no value/attribute table, so the canonical column
    /// schema is returned with an empty <c>features</c> array rather than a 404 — matching the
    /// document shape ArcGIS clients parse.
    /// </summary>
    public Task<IResult> GetRasterAttributeTableAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "raster-attribute-table", async _ =>
        {
            var primary = await _rasterStore.GetPrimaryRasterInfoAsync(layerId, cancellationToken);
            if (primary is null)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return (StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer."), 0);
            }

            var response = new RasterAttributeTableResponse
            {
                ObjectIdFieldName = "OBJECTID",
                Fields =
                [
                    new RasterAttributeTableField { Name = "OBJECTID", Type = "esriFieldTypeOID", Alias = "OBJECTID" },
                    new RasterAttributeTableField { Name = "Value", Type = "esriFieldTypeInteger", Alias = "Value" },
                    new RasterAttributeTableField { Name = "Count", Type = "esriFieldTypeInteger", Alias = "Count" },
                ],
                Features = [],
            };

            return (Results.Json(response, ImageServerJsonContext.Default.RasterAttributeTableResponse), 0);
        });

    /// <summary>
    /// Returns the read-only Esri <c>colormap</c> resource. Honua rasters are continuous and carry
    /// no intrinsic source colormap, so the resource reflects the active renderer: when the request
    /// supplies a <paramref name="renderingRule"/> whose Colormap function (explicit stops, a named
    /// <c>ColorrampName</c>, or an inline <c>Colorramp</c> object) resolves a colormap, that
    /// colormap is returned as <c>[value, r, g, b]</c> stops. A malformed/unsupported renderingRule
    /// surfaces its reason; otherwise the Esri not-available response is returned. This reuses the
    /// shared raster-function planner so the colormap matches what exportImage/legend render.
    /// </summary>
    public Task<IResult> GetColormapAsync(
        HttpContext context,
        int layerId,
        string? renderingRule,
        CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "colormap", _ =>
        {
            if (!TryResolveRendererColormap(renderingRule, out var colormap, out var invalidReason))
            {
                if (invalidReason is not null)
                {
                    ImageServerLog.RasterMetadataResourceFailed(
                        _logger, new InvalidOperationException(invalidReason), layerId, "colormap");
                    return Task.FromResult<(IResult, int)>(
                        (StandardErrorHelpers.CreateBadRequest(context, invalidReason), 0));
                }

                // No renderer colormap available. A continuous raster has no intrinsic colormap, so
                // return the Esri "not available" response rather than fabricating one.
                return Task.FromResult<(IResult, int)>((
                    StandardErrorHelpers.CreateBadRequest(
                        context,
                        "Colormap is not available for this image service. Supply a renderingRule whose Colormap " +
                        "function (explicit [value, r, g, b] stops, a ColorrampName, or an inline Colorramp object) " +
                        "resolves a colormap."),
                    0));
            }

            var stops = BuildColormapStops(colormap);
            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, stops.Length);
            return Task.FromResult<(IResult, int)>((
                Results.Json(
                    new ColormapResourceResponse { Colormap = stops },
                    ImageServerJsonContext.Default.ColormapResourceResponse),
                stops.Length));
        });

    // Resolves the renderer colormap from a supplied renderingRule using the shared planner.
    // Returns false with a non-null invalidReason when the renderingRule is malformed/unsupported,
    // and false with a null invalidReason when no colormap is available (the not-available case).
    private static bool TryResolveRendererColormap(
        string? renderingRule,
        out RasterColormap colormap,
        out string? invalidReason)
    {
        colormap = null!;
        invalidReason = null;

        if (string.IsNullOrWhiteSpace(renderingRule))
        {
            return false;
        }

        RasterFunctionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize(
                renderingRule, ImageServerJsonContext.Default.RasterFunctionDocument);
        }
        catch (JsonException)
        {
            invalidReason = "renderingRule must be a valid raster function document.";
            return false;
        }

        if (document is null)
        {
            invalidReason = "renderingRule must be a valid raster function document.";
            return false;
        }

        var mapping = ImageServerRasterFunctionPlanner.MapRenderingRule(document);
        if (!mapping.Supported)
        {
            invalidReason = mapping.Reason ?? "renderingRule is not supported on this service.";
            return false;
        }

        if (mapping.Colormap is { Entries.Count: > 0 } resolved)
        {
            colormap = resolved;
            return true;
        }

        // Supported renderingRule that carries no Colormap function -> colormap not available.
        return false;
    }

    private static int[][] BuildColormapStops(RasterColormap colormap)
        => colormap.Entries
            .OrderBy(static e => e.Value)
            .Select(static e => new[]
            {
                (int)Math.Round(e.Value, MidpointRounding.AwayFromZero),
                e.Red,
                e.Green,
                e.Blue,
            })
            .ToArray();

    private async Task<RasterStatistics[]> ResolveStatisticsAsync(
        int layerId,
        ImageServerV2Lookups.ResolvedImageLayer resolved,
        CancellationToken cancellationToken)
    {
        var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
        if (rasters.Length == 0)
        {
            return [];
        }

        if (rasters.Length == 1)
        {
            return await _rasterStore.GetStatisticsAsync(layerId, rasters[0].Id, bands: null, cancellationToken: cancellationToken);
        }

        var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
        return await _rasterStore.GetMosaicStatisticsAsync(
            layerId, rasters.Select(r => r.Id).ToArray(), mergeStrategy, bands: null, cancellationToken: cancellationToken);
    }

    private async Task<IResult> ExecuteAsync(
        HttpContext context,
        int layerId,
        string operationName,
        Func<ImageServerV2Lookups.ResolvedImageLayer, Task<(IResult Result, int Count)>> body)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            operationName,
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, operationName);

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(context.RequestAborted).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            var (result, count) = await body(resolved).ConfigureAwait(false);
            scope.SetSuccess(count);
            return result;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally generic: this is a top-level protocol request handler; any
        // unexpected failure (parsing bugs, provider errors, etc.) must map to a
        // generic 500 rather than crash the host or leak internals to the client.
        catch (Exception ex)
        {
            ImageServerLog.RasterMetadataResourceFailed(_logger, ex, layerId, operationName);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the raster metadata resource.");
        }
    }
}
