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
using SkiaSharp;

namespace Honua.Protocols.GeoServices.ImageServer.Handlers;

/// <summary>
/// Handler for the Esri Image Server <c>legend</c> endpoint. Generates a small
/// equal-interval class breaks legend for the layer's primary raster using the
/// stretched range of the first band as the value space. The renderer is
/// intentionally fixed (5-class viridis-style ramp) until 520-D's renderer
/// abstraction supports user-defined classes.
/// </summary>
internal sealed class ImageServerLegendHandler
{
    /// <summary>Number of equal-interval classes when no renderer is supplied.</summary>
    private const int DefaultClassCount = 5;

    /// <summary>
    /// Default colour ramp for the legend swatches. Picked to match a viridis-like
    /// progression so that legends remain readable on light and dark backgrounds.
    /// </summary>
    private static readonly SKColor[] DefaultColorRamp =
    [
        new SKColor(68, 1, 84),
        new SKColor(59, 82, 139),
        new SKColor(33, 144, 140),
        new SKColor(94, 201, 98),
        new SKColor(253, 231, 37),
    ];

    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly IRasterStore _rasterStore;
    private readonly IImageServerLegendSwatchBuilder _swatchBuilder;
    private readonly ILogger<ImageServerLegendHandler> _logger;

    public ImageServerLegendHandler(
        IMetadataV2GraphProvider graphProvider,
        IRasterStore rasterStore,
        IImageServerLegendSwatchBuilder swatchBuilder,
        ILogger<ImageServerLegendHandler> logger)
    {
        _graphProvider = graphProvider ?? throw new ArgumentNullException(nameof(graphProvider));
        _rasterStore = rasterStore ?? throw new ArgumentNullException(nameof(rasterStore));
        _swatchBuilder = swatchBuilder ?? throw new ArgumentNullException(nameof(swatchBuilder));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Builds the legend payload for the layer's primary raster.
    /// </summary>
    public async Task<IResult> GetLegendAsync(
        HttpContext context,
        int layerId,
        CancellationToken cancellationToken)
    {
        using var scope = HonuaTelemetryScope.StartFeature(
            "legend",
            HonuaTelemetry.Protocols.ImageServer,
            layerId.ToString(CultureInfo.InvariantCulture));
        scope.WithTag(HonuaTelemetry.Tags.Operation, "get-legend");

        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            if (ImageServerV2Lookups.FindByLayerIndex(snapshot, layerId) is not { } resolved)
            {
                ImageServerLog.LayerNotFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "Layer not found.");
            }

            var rasters = await _rasterStore.ListRastersAsync(layerId, cancellationToken);
            if (rasters.Length == 0)
            {
                ImageServerLog.NoRastersFound(_logger, layerId);
                return StandardErrorHelpers.CreateNotFound(context, "No rasters found for layer.");
            }

            var mergeStrategy = ImageServerV2Lookups.ResolveMergeStrategy(resolved.Resource, mosaicRule: null);
            var statistics = rasters.Length == 1
                ? await _rasterStore.GetStatisticsAsync(layerId, rasters[0].Id, bands: null, cancellationToken)
                : await _rasterStore.GetMosaicStatisticsAsync(
                    layerId,
                    rasters.Select(r => r.Id).ToArray(),
                    mergeStrategy,
                    bands: null,
                    cancellationToken);
            var swatches = BuildSwatches(statistics);

            var response = new LegendResponse
            {
                Layers =
                [
                    new LegendLayer
                    {
                        LayerId = 0,
                        LayerName = resolved.DisplayName,
                        LayerType = "Raster Layer",
                        MinScale = 0,
                        MaxScale = 0,
                        Legend = swatches,
                    },
                ],
            };

            ImageServerLog.LegendGenerated(_logger, layerId, swatches.Length);
            scope.SetSuccess(swatches.Length);

            return Results.Json(response, ImageServerJsonContext.Default.LegendResponse);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ImageServerLog.LegendFailed(_logger, ex, layerId);
            scope.RecordException(ex);
            return StandardErrorHelpers.CreateInternalServerError(
                context,
                "An error occurred while generating the legend.");
        }
    }

    private LegendEntry[] BuildSwatches(RasterStatistics[] statistics)
    {
        // Use band 1 stats when available so the swatches reflect actual pixel ranges.
        var firstBand = statistics.Length > 0 ? statistics[0] : default;
        var min = firstBand.MinValue ?? 0;
        var max = firstBand.MaxValue ?? 255;
        if (max <= min)
        {
            // Avoid divide-by-zero on uniform rasters by collapsing to a single class.
            var swatch = _swatchBuilder.Render(DefaultColorRamp[0]);
            return
            [
                new LegendEntry
                {
                    Label = FormattableString.Invariant($"{min:0.##}"),
                    ImageData = swatch.Base64Png,
                    Width = swatch.Width,
                    Height = swatch.Height,
                    Values = [min, min],
                },
            ];
        }

        var step = (max - min) / DefaultClassCount;
        var entries = new LegendEntry[DefaultClassCount];
        for (var i = 0; i < DefaultClassCount; i++)
        {
            var classMin = min + step * i;
            var classMax = i == DefaultClassCount - 1 ? max : min + step * (i + 1);
            var color = DefaultColorRamp[i % DefaultColorRamp.Length];
            var swatch = _swatchBuilder.Render(color);
            entries[i] = new LegendEntry
            {
                Label = FormattableString.Invariant($"{classMin:0.##} – {classMax:0.##}"),
                ImageData = swatch.Base64Png,
                Width = swatch.Width,
                Height = swatch.Height,
                Values = [classMin, classMax],
            };
        }

        return entries;
    }
}
