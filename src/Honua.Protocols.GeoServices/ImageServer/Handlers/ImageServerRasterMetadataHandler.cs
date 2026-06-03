// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
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
            var statistics = await ResolveStatisticsAsync(layerId, resolved, cancellationToken);
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
                    layerId, rasters[0].Id, bands: null, DefaultBinCount, cancellationToken);
            }
            else
            {
                var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
                histograms = await _rasterStore.GetMosaicHistogramsAsync(
                    layerId, rasters.Select(r => r.Id).ToArray(), mergeStrategy, bands: null, DefaultBinCount, cancellationToken);
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
                    Name = "Clip",
                    Description = "Clips the raster to a clipping geometry or extent.",
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
            return await _rasterStore.GetStatisticsAsync(layerId, rasters[0].Id, bands: null, cancellationToken);
        }

        var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
        return await _rasterStore.GetMosaicStatisticsAsync(
            layerId, rasters.Select(r => r.Id).ToArray(), mergeStrategy, bands: null, cancellationToken);
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
