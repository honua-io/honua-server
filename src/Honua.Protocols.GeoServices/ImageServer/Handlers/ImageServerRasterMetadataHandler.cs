// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Raster.Abstractions;
using Honua.Core.Features.Raster.Domain;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Services;
using Honua.Infrastructure.Models;
using Honua.ServiceDefaults;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

internal sealed class ImageServerRasterMetadataHandler
{
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

    public Task<IResult> GetStatisticsAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "statistics", async resolved =>
        {
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                return (Results.Json(
                    new StatisticsResourceResponse { Statistics = [] },
                    ImageServerJsonContext.Default.StatisticsResourceResponse), 0);
            }

            var rasterIds = rasters.Select(r => r.Id).ToArray();
            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(
                resolved.Resource, mosaicRule: null);
            var schemaName = context.RequestServices.GetService<ISchemaContext>()?.CurrentSchema;
            var statistics = await ImageServerStatisticsBudget.ResolveAsync(
                context.RequestServices.GetRequiredService<IServiceScopeFactory>(),
                ImageServerStatisticsBudget.CreateStatisticsOperationKey(
                    schemaName, layerId, rasterIds, mergeStrategy),
                schemaName,
                (services, ct) =>
                {
                    var rasterStore = services.GetRequiredService<IRasterStore>();
                    return rasterIds.Length == 1
                        ? rasterStore.GetStatisticsAsync(
                            layerId, rasterIds[0], bands: null, cancellationToken: ct)
                        : rasterStore.GetMosaicStatisticsAsync(
                            layerId, rasterIds, mergeStrategy, bands: null, cancellationToken: ct);
                },
                onBudgetExceeded: ImageServerStatisticsBudget.MarkResponseIncomplete(context.Response, _logger, layerId),
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

    public Task<IResult> GetHistogramsAsync(HttpContext context, int layerId, CancellationToken cancellationToken)
        => ExecuteAsync(context, layerId, "histograms", async resolved =>
        {
            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return (StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer."), 0);
            }

            var entries = await ReadHistogramsAsync(layerId, rasters,
                ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null),
                cancellationToken).ConfigureAwait(false);

            ImageServerLog.StatisticsHistogramsComputed(_logger, layerId, entries.Length);
            return (Results.Json(new HistogramsResourceResponse { Histograms = entries },
                ImageServerJsonContext.Default.HistogramsResourceResponse), entries.Length);
        });

    // The caller has already authorized and resolved the publication. SOAP passes
    // that exact publication's mosaic policy instead of re-resolving a storage ID.
    internal async Task<BandHistogram[]> ReadHistogramsAsync(
        int layerId,
        IReadOnlyList<RasterInfo> rasters,
        RasterMergeStrategy mergeStrategy,
        CancellationToken cancellationToken,
        int[]? bands = null)
    {
        // Bounded: a cold-cache mosaic histogram pass carries the same unbounded-compute
        // risk as statistics (#2991) — degrade to an empty histogram set rather than
        // hang past the host's own request/function deadline.
        var histograms = await ImageServerStatisticsBudget.ResolveCancellableAsync(
            ct => rasters.Count == 1
                ? _rasterStore.GetHistogramsAsync(
                layerId, rasters[0].Id, bands, DefaultBinCount, cancellationToken: ct)
                : _rasterStore.GetMosaicHistogramsAsync(
                layerId,
                rasters.Select(r => r.Id).ToArray(),
                mergeStrategy,
                bands,
                DefaultBinCount,
                cancellationToken: ct),
            onBudgetExceeded: () => ImageServerLog.StatisticsComputeBudgetExceeded(
                _logger, layerId, ImageServerStatisticsBudget.Timeout.TotalSeconds),
            cancellationToken);

        histograms = histograms.Where(histogram => bands is null || bands.Contains(histogram.Band))
            .OrderBy(static histogram => histogram.Band).ToArray();
        var entries = new BandHistogram[histograms.Length];
        for (var i = 0; i < histograms.Length; i++)
        {
            var h = histograms[i];
            entries[i] = ImageServerHistogramProjection.Project(h);
        }

        return entries;
    }

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
            if (ImageServerV2Lookups.FindByStorageLayerId(snapshot, layerId, context) is not { } resolved)
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ImageServerLog.RasterMetadataResourceFailed(_logger, ex, layerId, operationName);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while retrieving the raster metadata resource.");
        }
    }
}
