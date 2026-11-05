// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

internal enum TileGridKind
{
    WebMercatorQuad = 0,

    WorldCrs84Quad = 1
}

// An instant selection sets both bounds to the same value, and the selection is recorded only; it is not yet a datacube slice.
internal readonly record struct VerticalSelection(double Min, double Max, string RawValue)
{
    public bool IsInstant => Min.Equals(Max);

    public static VerticalSelection FromValue(double value, string rawValue)
        => new(value, value, rawValue);
}

internal sealed class RasterRenderCapacityExceededException(string message, int retryAfterSeconds)
    : Exception(message)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}

internal sealed class RasterRenderSurfaceException(string message) : Exception(message);

internal static class RasterMapRenderingPipeline
{
    internal sealed class RasterStylePlan
    {
        public required MapLibreStyleLayer[] StyleLayers { get; init; }

        public required string[] ReferencedFields { get; init; }

        public ResolvedCircleStyle? SimpleCircleStyle { get; init; }
    }

    private sealed class CachedRasterStylePlan
    {
        public required int StyleVersion { get; init; }

        public string? MapLibreStyleJson { get; init; }

        public required RasterStylePlan Plan { get; init; }

        public bool Matches(LayerStyleDefinition? style) =>
            StyleVersion == (style?.StyleVersion ?? 0) &&
            string.Equals(MapLibreStyleJson, style?.MapLibreStyleJson, StringComparison.Ordinal);
    }

    internal const int TileSize = 256;
    internal const int TileSrid = 3857;
    internal const int GeographicTileSrid = 4326;
    internal const int MaxFeaturesPerLayer = 10_000;
    private const string InvalidSpatialReferenceMessage = "Invalid spatial reference.";
    private static readonly ConcurrentDictionary<int, CachedRasterStylePlan> _rasterStylePlanCache = new();

    internal readonly record struct RenderLayerDescriptor(
        int LayerId,
        bool HasGeometry,
        MetadataV2GeometryType GeometryType,
        VerticalSelection? VerticalSelection = null,
        string? StableOrderField = null,
        MetadataV2FieldType? StableOrderFieldType = null,
        ImmutableArray<string>? EnforcedMaskedFields = null);

    internal static RenderLayerDescriptor CreateRenderLayerDescriptorFromV2(
        int layerId,
        bool hasGeometry,
        MetadataV2GeometryType geometryType,
        VerticalSelection? verticalSelection = null,
        string? stableOrderField = null,
        MetadataV2FieldType? stableOrderFieldType = null)
        => new(layerId, hasGeometry, geometryType, verticalSelection, stableOrderField, stableOrderFieldType);

    internal static Task<RasterTileRenderResult> RenderRasterTileV2Async(
        HttpContext context,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int z,
        int y,
        int x,
        int maxFeatures,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters = null)
        => RenderRasterTileCoreForHttpAsync(
            context,
            serviceSrid,
            renderLayers,
            TileSrid,
            TileMath.GetTileBounds(x, y, z),
            maxFeatures,
            cancellationToken,
            layerTemporalFilters);

    internal static Task<RasterTileRenderResult> RenderRasterTileForGridAsync(
        HttpContext context,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int z,
        int y,
        int x,
        int maxFeatures,
        TileGridKind grid,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters = null)
    {
        var isGeographicGrid = grid == TileGridKind.WorldCrs84Quad;
        var tileSrid = isGeographicGrid ? GeographicTileSrid : TileSrid;
        var tileBounds = isGeographicGrid
            ? TileMath.GetTileBoundsGeographic(x, y, z)
            : TileMath.GetTileBounds(x, y, z);
        return RenderRasterTileCoreForHttpAsync(
            context,
            serviceSrid,
            renderLayers,
            tileSrid,
            tileBounds,
            maxFeatures,
            cancellationToken,
            layerTemporalFilters);
    }

    // Built-in gridsets stay on the other overload so their bytes do not change; this overload exists because the enum cannot name an operator-defined gridset.
    internal static Task<RasterTileRenderResult> RenderRasterTileForGridAsync(
        HttpContext context,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int z,
        int y,
        int x,
        int maxFeatures,
        GridGeometry gridGeometry,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters = null)
    {
        ArgumentNullException.ThrowIfNull(gridGeometry);
        var tileBounds = gridGeometry.GetTileBounds(x, y, z)
            ?? throw new ArgumentOutOfRangeException(nameof(z), "Requested level is not part of the tile matrix set.");
        return RenderRasterTileCoreForHttpAsync(
            context,
            serviceSrid,
            renderLayers,
            gridGeometry.Srid,
            tileBounds,
            maxFeatures,
            cancellationToken,
            layerTemporalFilters);
    }

#pragma warning disable CA1068 // legacy callers/tests pass cancellation before optional temporal filters
    private static async Task<RasterTileRenderResult> RenderRasterTileCoreAsync(
        IServiceProvider services,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int tileSrid,
        TileBounds tileBounds,
        int maxFeatures,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters)
#pragma warning restore CA1068
    {
        var renderExtent = new SkiaMapRenderer.RenderExtent(
            tileBounds.XMin,
            tileBounds.YMin,
            tileBounds.XMax,
            tileBounds.YMax);

        await using var renderLease = await services
            .GetRequiredService<RasterRenderCapacityLimiter>()
            .TryAcquireAsync(TileSize, TileSize, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is null)
        {
            // The core is HTTP-independent: signal the failure through a typed exception so an
            // HTTP caller can shape a 503 while the durable tile-export producer fails the job.
            throw new RasterRenderCapacityExceededException(
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds);
        }

        if (renderLayers.Count == 0)
        {
            using var renderer = new SkiaMapRenderer();
            var emptyImage = renderer.RenderMap(
                [],
                [],
                renderExtent,
                TileSize,
                TileSize,
                true,
                null,
                MetadataV2GeometryType.None,
                RenderZoom.NotDerivable("the tile has no render layers"));

            return RasterTileRenderResult.Success(emptyImage, 0);
        }

        var featureReader = services.GetRequiredService<IFeatureReader>();
        var styleCatalog = services.GetRequiredService<ILayerStyleCatalog>();

        // The tile envelope is expressed in the gridset CRS (3857 for WebMercatorQuad, 4326 for
        // WorldCRS84Quad). Build the spatial filter in that CRS and let the canonical query
        // pipeline reproject the data from the storage CRS (SpatialReferenceSrid) into the gridset
        // CRS (OutputSrid). This mirrors the OGC API Tiles raster path so no geodesy is duplicated.
        var spatialFilter = CreateBboxSpatialFilter(renderExtent, tileSrid);
        var totalFeatureCount = 0;

        using var surface = SKSurface.Create(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            throw new RasterRenderSurfaceException("Failed to allocate render surface.");
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var transform = SkiaMapRenderer.BuildTransform(renderExtent, TileSize, TileSize);

        // The tile matrix level is not the MapLibre zoom: these tiles are TileSize (256) pixels, so
        // a client consuming them as a 256px source sits one camera zoom below the matrix level.
        // Deriving from the tile envelope keeps the gate aligned with that camera zoom and with the
        // other protocols rendering the same envelope.
        var renderZoom = RenderZoom.FromExtent(renderExtent, TileSize, TileSize, tileSrid);
        RecordRenderZoom(renderZoom);

        for (var layerIndex = 0; layerIndex < renderLayers.Count; layerIndex++)
        {
            var layer = renderLayers[layerIndex];
            cancellationToken.ThrowIfCancellationRequested();

            // DOCUMENTED DIVERGENCE (#1792 Shape A): the elevation/vertical selection
            // resolved from the WMTS `elevation=` dimension or the OGC API Tiles
            // `subset=Z(...)` axis is RECORDED (telemetry-tagged) here but is NOT applied
            // to the vector render below — there is no Z-aware vector source yet. This is
            // an intentional, honest divergence from the shared pipeline per AGENTS.md
            // ("intentionally diverges" rule): we validate + record the vertical dimension
            // instead of advertising-but-ignoring it (WMTS) or blanket-rejecting it (Tiles).
            // Actually slicing a Zarr datacube into the raster is the deferred Shape B
            // follow-up (after #1790). Direct endpoint tests cover this record-but-don't-render
            // behavior.
            if (layer.VerticalSelection is { } verticalSelection)
            {
                System.Diagnostics.Activity.Current?.SetTag(
                    "honua.tile.vertical_selection",
                    verticalSelection.RawValue);
            }

            if (!layer.HasGeometry)
            {
                continue;
            }

            var stylePlan = await GetRasterStylePlanAsync(
                styleCatalog,
                layer.LayerId,
                cancellationToken).ConfigureAwait(false);
            if (!StyleTranslator.IsAnyLayerInZoomRange(stylePlan.StyleLayers, renderZoom))
            {
                continue;
            }

            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                serviceSrid,
                tileSrid,
                maxFeatures,
                temporalFilter: layerTemporalFilters is { Count: > 0 } && layerIndex < layerTemporalFilters.Count
                    ? layerTemporalFilters[layerIndex]
                    : null,
                stableOrderField: layer.StableOrderField,
                stableOrderFieldType: layer.StableOrderFieldType);
            featureQuery = featureQuery with { EnforcedMaskedFields = layer.EnforcedMaskedFields };
            FeatureQuerySecurity.Validate(featureQuery);

            var renderedPointCount = await TryRenderAllRasterPointPagesAsync(
                canvas,
                featureReader,
                layer.LayerId,
                layer.GeometryType,
                stylePlan,
                featureQuery,
                renderExtent,
                TileSize,
                TileSize,
                transform,
                cancellationToken).ConfigureAwait(false);
            if (renderedPointCount >= 0)
            {
                totalFeatureCount += renderedPointCount;
                continue;
            }

            await foreach (var features in QueryAllRasterFeaturePagesAsync(
                               featureReader,
                               layer.LayerId,
                               featureQuery,
                               cancellationToken).ConfigureAwait(false))
            {
                totalFeatureCount += features.Length;
                RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, layer.GeometryType, renderZoom);
            }
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, "png");
        return RasterTileRenderResult.Success(imageBytes, totalFeatureCount);
    }

#pragma warning disable CA1068 // matches the core: cancellation precedes optional temporal filters
    private static async Task<RasterTileRenderResult> RenderRasterTileCoreForHttpAsync(
        HttpContext context,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int tileSrid,
        TileBounds tileBounds,
        int maxFeatures,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters)
#pragma warning restore CA1068
    {
        try
        {
            return await RenderRasterTileCoreAsync(
                context.RequestServices,
                serviceSrid,
                renderLayers,
                tileSrid,
                tileBounds,
                maxFeatures,
                cancellationToken,
                layerTemporalFilters).ConfigureAwait(false);
        }
        catch (RasterRenderCapacityExceededException exception)
        {
            return RasterTileRenderResult.Failure(StandardErrorHelpers.CreateServiceUnavailable(
                context, exception.Message, exception.RetryAfterSeconds));
        }
        catch (RasterRenderSurfaceException exception)
        {
            return RasterTileRenderResult.Failure(
                StandardErrorHelpers.CreateInternalServerError(context, exception.Message));
        }
    }

    internal static async Task<byte[]> RenderRasterTileForExportAsync(
        IServiceProvider services,
        int serviceSrid,
        IReadOnlyList<RenderLayerDescriptor> renderLayers,
        int z,
        int y,
        int x,
        int maxFeatures,
        CancellationToken cancellationToken,
        IReadOnlyList<TemporalFilter?>? layerTemporalFilters = null)
    {
        var result = await RenderRasterTileCoreAsync(
            services,
            serviceSrid,
            renderLayers,
            TileSrid,
            TileMath.GetTileBounds(x, y, z),
            maxFeatures,
            cancellationToken,
            layerTemporalFilters).ConfigureAwait(false);
        return result.ImageBytes;
    }

    internal readonly record struct VectorCollectionRenderResult(
        bool IsSuccess,
        byte[] ImageBytes,
        int FeatureCount,
        IResult? Error)
    {
        public static VectorCollectionRenderResult Success(byte[] imageBytes, int featureCount)
            => new(true, imageBytes, featureCount, null);

        public static VectorCollectionRenderResult Failure(IResult error)
            => new(false, [], 0, error);
    }

    internal static async Task<VectorCollectionRenderResult> RenderVectorCollectionAsync(
        HttpContext context,
        int layerId,
        MetadataV2GeometryType geometryType,
        int serviceSrid,
        string? mapLibreStyleJson,
        SkiaMapRenderer.RenderExtent requestExtent,
        int requestSrid,
        int imageWidth,
        int imageHeight,
        string format,
        bool transparent,
        SKColor? backgroundColor,
        TemporalFilter? temporalFilter,
        CancellationToken cancellationToken)
    {
        await using var renderLease = await context.RequestServices
            .GetRequiredService<RasterRenderCapacityLimiter>()
            .TryAcquireAsync(imageWidth, imageHeight, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is null)
        {
            return VectorCollectionRenderResult.Failure(StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds));
        }

        // Reproject the request extent into the storage CRS the feature reader expects,
        // mirroring WMS GetMap's per-layer handling.
        var queryExtent = requestExtent;
        if (requestSrid != serviceSrid)
        {
            var extentTransformResult = await TryTransformExtentAsync(
                context,
                requestExtent,
                requestSrid,
                serviceSrid,
                cancellationToken).ConfigureAwait(false);
            if (!extentTransformResult.IsSuccess)
            {
                return VectorCollectionRenderResult.Failure(
                    StandardErrorHelpers.CreateBadRequest(context, extentTransformResult.Error ?? InvalidSpatialReferenceMessage));
            }

            queryExtent = extentTransformResult.Extent;
        }

        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return VectorCollectionRenderResult.Failure(
                StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface."));
        }

        var canvas = surface.Canvas;
        var effectiveTransparent = transparent && string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);
        canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor ?? SKColors.White);

        var totalFeatureCount = 0;
        if (geometryType != MetadataV2GeometryType.None)
        {
            var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
            var transform = SkiaMapRenderer.BuildTransform(requestExtent, imageWidth, imageHeight);
            var renderZoom = await DeriveRenderZoomAsync(
                context,
                requestExtent,
                requestSrid,
                imageWidth,
                imageHeight,
                cancellationToken).ConfigureAwait(false);
            var stylePlan = BuildRasterStylePlanFromJson(mapLibreStyleJson);
            var spatialFilter = CreateBboxSpatialFilter(queryExtent, serviceSrid);
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                serviceSrid,
                requestSrid,
                MaxFeaturesPerLayer,
                temporalFilter: temporalFilter);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                canvas,
                featureReader,
                layerId,
                geometryType,
                stylePlan,
                featureQuery,
                requestExtent,
                imageWidth,
                imageHeight,
                transform,
                cancellationToken).ConfigureAwait(false);
            if (renderedPointCount >= 0)
            {
                totalFeatureCount += renderedPointCount;
            }
            else
            {
                var features = await QueryRasterFeaturesAsync(featureReader, layerId, featureQuery, cancellationToken)
                    .ConfigureAwait(false);
                if (features.Length > 0)
                {
                    totalFeatureCount += features.Length;
                    RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, geometryType, renderZoom);
                }
            }
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, format);
        return VectorCollectionRenderResult.Success(imageBytes, totalFeatureCount);
    }

    internal readonly record struct BoundStyleVectorLayer(
        int LayerId,
        MetadataV2GeometryType GeometryType,
        int StorageSrid,
        string? ExplicitMapLibreStyleJson,
        TemporalFilter? TemporalFilter = null);

    internal static async Task<byte[]> RenderBoundStyleVectorLayersAsync(
        IFeatureReader featureReader,
        ILayerStyleCatalog styleCatalog,
        IReadOnlyList<BoundStyleVectorLayer> layers,
        SkiaMapRenderer.RenderExtent requestExtent,
        int requestSrid,
        int imageWidth,
        int imageHeight,
        int maxFeatures,
        string format,
        bool transparent,
        SKColor? backgroundColor,
        CancellationToken cancellationToken)
    {
        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            throw new InvalidOperationException($"Skia failed to allocate a render surface at {imageWidth}x{imageHeight}.");
        }

        var canvas = surface.Canvas;
        var effectiveTransparent = transparent && string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);
        canvas.Clear(effectiveTransparent ? SKColors.Transparent : backgroundColor ?? SKColors.White);

        var transform = SkiaMapRenderer.BuildTransform(requestExtent, imageWidth, imageHeight);
        var renderZoom = RenderZoom.FromExtent(requestExtent, imageWidth, imageHeight, requestSrid);
        RecordRenderZoom(renderZoom);

        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (layer.GeometryType == MetadataV2GeometryType.None)
            {
                continue;
            }

            var stylePlan = layer.ExplicitMapLibreStyleJson is { } explicitJson
                ? BuildRasterStylePlanFromJson(explicitJson)
                : await GetRasterStylePlanAsync(styleCatalog, layer.LayerId, cancellationToken).ConfigureAwait(false);

            var spatialFilter = CreateBboxSpatialFilter(requestExtent, requestSrid);
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                layer.StorageSrid,
                requestSrid,
                maxFeatures,
                temporalFilter: layer.TemporalFilter);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                canvas,
                featureReader,
                layer.LayerId,
                layer.GeometryType,
                stylePlan,
                featureQuery,
                requestExtent,
                imageWidth,
                imageHeight,
                transform,
                cancellationToken).ConfigureAwait(false);
            if (renderedPointCount >= 0)
            {
                continue;
            }

            var features = await QueryRasterFeaturesAsync(featureReader, layer.LayerId, featureQuery, cancellationToken)
                .ConfigureAwait(false);
            if (features.Length == 0)
            {
                continue;
            }

            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, layer.GeometryType, renderZoom);
        }

        return SkiaMapRenderer.EncodeSurface(surface, format);
    }

    internal static void RenderLayerToCanvas(
        SKCanvas canvas,
        ImmutableArray<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        Func<double, double, SKPoint> transform,
        MetadataV2GeometryType geometryType,
        RenderZoom zoom)
    {
        if (styleLayers.Length > 0)
        {
            foreach (var styleLayer in styleLayers)
            {
                if (styleLayer.Type == null || !StyleTranslator.ShouldRenderLayer(styleLayer, zoom))
                {
                    continue;
                }

                if (SkiaFeatureDrawing.TryRenderBatchedStyleLayer(canvas, features, styleLayer, transform, zoom))
                {
                    continue;
                }

                foreach (var feature in features)
                {
                    if (feature.Geometry == null || feature.Geometry.Length < 5)
                    {
                        continue;
                    }

                    if (styleLayer.Filter is { } filter && !EvaluateFilter(filter, feature.Attributes, zoom))
                    {
                        continue;
                    }

                    SkiaFeatureDrawing.RenderStyledFeature(canvas, feature, styleLayer, transform, zoom);
                }
            }
        }
        else
        {
            var (fill, stroke) = StyleTranslator.CreateDefaultPaints(geometryType);
            using var fillDisposable = fill;
            using var strokeDisposable = stroke;

            if (geometryType == MetadataV2GeometryType.Point)
            {
                SkiaFeatureDrawing.RenderDefaultPoints(canvas, features, transform, fill);
                return;
            }

            foreach (var feature in features)
            {
                if (feature.Geometry == null || feature.Geometry.Length < 5)
                {
                    continue;
                }

                var result = WkbToSkiaConverter.Convert(feature.Geometry, transform);
                SkiaFeatureDrawing.RenderConversionResult(canvas, result, fill, stroke);
            }
        }
    }


    internal static async Task<RasterStylePlan> GetRasterStylePlanAsync(
        ILayerStyleCatalog styleCatalog,
        int layerId,
        CancellationToken cancellationToken)
    {
        var style = await styleCatalog.GetLayerStyleAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (_rasterStylePlanCache.TryGetValue(layerId, out var cached) &&
            cached.Matches(style))
        {
            return cached.Plan;
        }

        var plan = BuildRasterStylePlan(style);
        _rasterStylePlanCache[layerId] = new CachedRasterStylePlan
        {
            StyleVersion = style?.StyleVersion ?? 0,
            MapLibreStyleJson = style?.MapLibreStyleJson,
            Plan = plan
        };

        return plan;
    }

    private static RasterStylePlan BuildRasterStylePlan(LayerStyleDefinition? style)
        => BuildRasterStylePlanFromJson(style?.MapLibreStyleJson);

    internal static RasterStylePlan BuildRasterStylePlanFromJson(string? mapLibreStyleJson)
    {
        var styleLayers = StyleTranslator.ParseStyleLayers(mapLibreStyleJson);
        var referencedFields = StyleTranslator.CollectReferencedFields(styleLayers);

        return new RasterStylePlan
        {
            StyleLayers = styleLayers,
            ReferencedFields = referencedFields,
            SimpleCircleStyle = TryResolveSimpleCircleStyle(styleLayers, referencedFields)
        };
    }

    // A zoom-dependent circle style is not baked into the cached plan, or the first request's zoom would freeze the radius for every later request.
    private static ResolvedCircleStyle? TryResolveSimpleCircleStyle(
        MapLibreStyleLayer[] styleLayers,
        string[] referencedFields)
    {
        if (styleLayers.Length != 1 ||
            !string.Equals(styleLayers[0].Type, "circle", StringComparison.Ordinal) ||
            styleLayers[0].Filter is not null ||
            referencedFields.Length != 0 ||
            StyleTranslator.UsesZoomExpression(styleLayers[0]))
        {
            return null;
        }

        var circleStyle = StyleTranslator.ResolveCircleStyle(
            styleLayers[0],
            ImmutableDictionary<string, object?>.Empty,
            RenderZoom.NotDerivable("a cached style plan is built without a render zoom"));
        return circleStyle.Radius > 0 ? circleStyle : null;
    }

    internal static FeatureQuery CreateRasterFeatureQuery(
        RasterStylePlan stylePlan,
        SpatialFilter spatialFilter,
        int spatialReferenceSrid,
        int? outputSrid,
        int limit,
        SqlFragment? sqlFilter = null,
        TemporalFilter? temporalFilter = null,
        string? stableOrderField = null,
        MetadataV2FieldType? stableOrderFieldType = null)
    {
        var referencedFields = stylePlan.ReferencedFields;

        return new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = spatialReferenceSrid,
            OutputSrid = outputSrid,
            Limit = limit,
            SqlFilter = sqlFilter,
            TemporalFilter = temporalFilter,
            OrderBy = string.IsNullOrWhiteSpace(stableOrderField)
                ? null
                : [new OrderByClause(stableOrderField, ascending: true, stableOrderFieldType)],
            OutFields = referencedFields.Length > 0 ? ImmutableArray.CreateRange(referencedFields) : null,
            ExcludeAttributes = referencedFields.Length == 0
        };
    }

    internal static async Task<int> TryRenderRasterPointFastPathAsync(
        SKCanvas canvas,
        IFeatureReader featureReader,
        int layerId,
        MetadataV2GeometryType geometryType,
        RasterStylePlan stylePlan,
        FeatureQuery featureQuery,
        SkiaMapRenderer.RenderExtent renderExtent,
        int imageWidth,
        int imageHeight,
        Func<double, double, SKPoint> transform,
        CancellationToken cancellationToken)
    {
        if (featureReader is not IRasterPointReader rasterPointReader ||
            !TryCreateRasterPointFastPathQuery(
                geometryType,
                stylePlan,
                featureQuery,
                renderExtent,
                imageWidth,
                imageHeight,
                out var pointQuery,
                out var circleStyle))
        {
            return -1;
        }

        var points = await rasterPointReader.QueryProjectedPointsAsync(layerId, pointQuery, cancellationToken).ConfigureAwait(false);
        if (points.Length == 0)
        {
            return 0;
        }

        RenderProjectedCirclePoints(canvas, points, transform, circleStyle);
        return points.Length;
    }

    internal static async Task<int> TryRenderAllRasterPointPagesAsync(
        SKCanvas canvas,
        IFeatureReader featureReader,
        int layerId,
        MetadataV2GeometryType geometryType,
        RasterStylePlan stylePlan,
        FeatureQuery featureQuery,
        SkiaMapRenderer.RenderExtent renderExtent,
        int imageWidth,
        int imageHeight,
        Func<double, double, SKPoint> transform,
        CancellationToken cancellationToken)
    {
        if (featureReader is not IRasterPointReader rasterPointReader ||
            !TryCreateRasterPointFastPathQuery(
                geometryType,
                stylePlan,
                featureQuery,
                renderExtent,
                imageWidth,
                imageHeight,
                out var pointQuery,
                out var circleStyle))
        {
            return -1;
        }

        var pageSize = Math.Max(1, pointQuery.Limit ?? MaxFeaturesPerLayer);
        var offset = pointQuery.Offset ?? 0;
        var total = 0;
        while (true)
        {
            var points = await rasterPointReader.QueryProjectedPointsAsync(
                layerId,
                pointQuery with { Limit = pageSize, Offset = offset },
                cancellationToken).ConfigureAwait(false);
            if (points.Length == 0)
            {
                break;
            }

            RenderProjectedCirclePoints(canvas, points, transform, circleStyle);
            total += points.Length;
            if (points.Length < pageSize)
            {
                break;
            }

            offset = checked(offset + points.Length);
        }

        return total;
    }

    private static bool TryCreateRasterPointFastPathQuery(
        MetadataV2GeometryType geometryType,
        RasterStylePlan stylePlan,
        FeatureQuery featureQuery,
        SkiaMapRenderer.RenderExtent renderExtent,
        int imageWidth,
        int imageHeight,
        out FeatureQuery pointQuery,
        out ResolvedCircleStyle circleStyle)
    {
        pointQuery = default;
        circleStyle = default!;

        if (geometryType != MetadataV2GeometryType.Point ||
            stylePlan.SimpleCircleStyle is null ||
            !featureQuery.ExcludeAttributes ||
            imageWidth <= 0 ||
            imageHeight <= 0 ||
            CoordinateTransformer.GetEffectiveWidth(renderExtent) <= 0 ||
            renderExtent.Height <= 0)
        {
            return false;
        }

        circleStyle = stylePlan.SimpleCircleStyle;
        var generalizationPixels = Math.Max(SkiaFeatureDrawing.PointGeneralizationPixels, circleStyle.Radius);
        var cellWidth = CoordinateTransformer.GetEffectiveWidth(renderExtent) / imageWidth * generalizationPixels;
        var cellHeight = renderExtent.Height / imageHeight * generalizationPixels;
        if (cellWidth <= 0 || cellHeight <= 0)
        {
            return false;
        }

        var spatialFilter = featureQuery.SpatialFilter is { SpatialRelationship: SpatialRelationship.Intersects } currentSpatialFilter
            ? currentSpatialFilter with { SpatialRelationship = SpatialRelationship.EnvelopeIntersects }
            : featureQuery.SpatialFilter;

        pointQuery = featureQuery with
        {
            SpatialFilter = spatialFilter,
            RasterPointGrid = new RasterPointGrid
            {
                OriginX = renderExtent.MinX,
                OriginY = renderExtent.MaxY,
                CellWidth = cellWidth,
                CellHeight = cellHeight
            }
        };

        return true;
    }

    internal static async Task<ImmutableArray<Feature>> QueryRasterFeaturesAsync(
        IFeatureReader featureReader,
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (featureReader is IPagedFeatureReader pagedFeatureReader &&
            query.Limit.HasValue &&
            query.Limit.Value != int.MaxValue)
        {
            var pagedResult = await pagedFeatureReader.QueryPageAsync(layerId, query, cancellationToken).ConfigureAwait(false);
            return pagedResult.Items;
        }

        var result = await featureReader.QueryAsync(layerId, query, cancellationToken).ConfigureAwait(false);
        return result.Items;
    }

    internal static async IAsyncEnumerable<ImmutableArray<Feature>> QueryAllRasterFeaturePagesAsync(
        IFeatureReader featureReader,
        int layerId,
        FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // ArcGIS raster operations do not define a feature-count truncation contract (#4044).
        // Treat the configured limit as a bounded page size, not a total cap: every page is
        // rendered immediately, while the request timeout token bounds total database/render work.
        var pageSize = Math.Max(1, query.Limit ?? MaxFeaturesPerLayer);
        var offset = query.Offset ?? 0;
        if (featureReader is not IPagedFeatureReader pagedFeatureReader)
        {
            while (true)
            {
                var result = await featureReader.QueryAsync(
                    layerId,
                    query with { Limit = pageSize, Offset = offset },
                    cancellationToken).ConfigureAwait(false);
                if (result.Items.IsDefaultOrEmpty)
                {
                    yield break;
                }

                yield return ApplyRasterFieldMasks(query, result.Items);
                if (result.Items.Length < pageSize)
                {
                    yield break;
                }

                offset = checked(offset + result.Items.Length);
            }
        }

        while (true)
        {
            var page = await pagedFeatureReader.QueryPageAsync(
                layerId,
                query with { Limit = pageSize, Offset = offset },
                cancellationToken).ConfigureAwait(false);
            if (page.Items.IsDefaultOrEmpty)
            {
                yield break;
            }

            yield return ApplyRasterFieldMasks(query, page.Items);
            if (!page.HasMoreResults)
            {
                yield break;
            }

            offset = checked(offset + page.Items.Length);
        }
    }

    private static ImmutableArray<Feature> ApplyRasterFieldMasks(FeatureQuery query, ImmutableArray<Feature> features)
    {
        if (query.EnforcedMaskedFields is not { IsDefaultOrEmpty: false } fields)
        {
            return features;
        }

        // Keep the render snapshot authoritative even if a provider rechecks live policy
        // or cannot enforce query-carried masks after that policy has been removed.
        var masks = fields.Where(static field => !string.IsNullOrWhiteSpace(field))
            .Select(static field => field.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return features.Select(feature => feature with
        {
            Attributes = feature.Attributes.RemoveRange(feature.Attributes.Keys.Where(masks.Contains))
        }).ToImmutableArray();
    }

    private static void RenderProjectedCirclePoints(
        SKCanvas canvas,
        ImmutableArray<ProjectedPoint> points,
        Func<double, double, SKPoint> transform,
        ResolvedCircleStyle circleStyle)
    {
        if (points.IsDefaultOrEmpty || circleStyle.Radius <= 0)
        {
            return;
        }

        var projectedPoints = new SKPoint[points.Length];

        for (var i = 0; i < points.Length; i++)
        {
            projectedPoints[i] = transform(points[i].X, points[i].Y);
        }

        if (points.Length >= SkiaFeatureDrawing.PointGeneralizationThreshold)
        {
            using var circlePaint = SkiaFeatureDrawing.CreateBatchedCirclePaint(
                circleStyle.FillColor,
                Math.Max(1f, circleStyle.Radius * 2f));
            SkiaFeatureDrawing.DrawPointBatch(canvas, projectedPoints, points.Length, circlePaint);

            if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
            {
                using var strokePaint = SkiaFeatureDrawing.CreateBatchedCirclePaint(
                    circleStyle.StrokeColor.Value,
                    Math.Max(1f, (circleStyle.Radius * 2f) + circleStyle.StrokeWidth));
                SkiaFeatureDrawing.DrawPointBatch(canvas, projectedPoints, points.Length, strokePaint);
            }

            return;
        }

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = circleStyle.FillColor,
            IsAntialias = true
        };
        SkiaFeatureDrawing.DrawCircleLoop(canvas, projectedPoints, points.Length, circleStyle.Radius, fillPaint);

        if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
        {
            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = circleStyle.StrokeColor.Value,
                StrokeWidth = circleStyle.StrokeWidth,
                IsAntialias = true
            };
            SkiaFeatureDrawing.DrawCircleLoop(canvas, projectedPoints, points.Length, circleStyle.Radius, strokePaint);
        }
    }

    internal static async Task<RenderZoom> DeriveRenderZoomAsync(
        HttpContext context,
        SkiaMapRenderer.RenderExtent extent,
        int srid,
        int imageWidth,
        int imageHeight,
        CancellationToken cancellationToken)
    {
        var zoom = RenderZoom.FromExtent(extent, imageWidth, imageHeight, srid);

        if (zoom.Level is null && !SpatialReferenceExtensions.IsWebMercatorSrid(srid))
        {
            var transformed = await TryTransformExtentAsync(context, extent, srid, TileSrid, cancellationToken)
                .ConfigureAwait(false);
            zoom = transformed.IsSuccess
                ? RenderZoom.FromWebMercatorExtent(transformed.Extent, imageWidth, imageHeight)
                : RenderZoom.NotDerivable(
                    $"the request extent could not be transformed from EPSG:{srid.ToString(System.Globalization.CultureInfo.InvariantCulture)} to Web Mercator");
        }

        RecordRenderZoom(zoom);
        return zoom;
    }

    internal static void RecordRenderZoom(RenderZoom zoom)
    {
        var activity = System.Diagnostics.Activity.Current;
        if (activity is null)
        {
            return;
        }

        if (zoom.Level is { } level)
        {
            activity.SetTag("honua.render.zoom", level);
        }
        else
        {
            activity.SetTag("honua.render.zoom_ungated_reason", zoom.NotDerivableReason);
        }
    }

    internal static int? TryParseSrid(string? sr)
        => SpatialReferenceHelpers.TryParseSrid(sr);

    internal static async Task<ExtentTransformResult> TryTransformExtentAsync(
        HttpContext context,
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            return ExtentTransformResult.Success(
                CoordinateTransformer.TransformExtent(extent, fromSrid, toSrid));
        }
        catch (NotSupportedException)
        {
            var coordinateTransformService = context.RequestServices.GetService<ICoordinateTransformService>();
            if (coordinateTransformService is not null)
            {
                var transformedExtent = await coordinateTransformService
                    .TransformExtentAsync(
                        extent.MinX,
                        extent.MinY,
                        extent.MaxX,
                        extent.MaxY,
                        fromSrid,
                        toSrid,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (transformedExtent.HasValue)
                {
                    return ExtentTransformResult.Success(new SkiaMapRenderer.RenderExtent(
                        transformedExtent.Value.MinX,
                        transformedExtent.Value.MinY,
                        transformedExtent.Value.MaxX,
                        transformedExtent.Value.MaxY));
                }
            }

            var connectionProvider = context.RequestServices.GetService<IAdoNetDatabaseConnectionProvider>();
            if (connectionProvider == null)
            {
                return ExtentTransformResult.Failure(InvalidSpatialReferenceMessage);
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Infrastructure.Rendering.RasterMapRenderingPipeline");

            var transformed = await TryTransformExtentWithPostGisAsync(
                connectionProvider,
                logger,
                extent,
                fromSrid,
                toSrid,
                cancellationToken);
            return transformed.HasValue
                ? ExtentTransformResult.Success(transformed.Value)
                : ExtentTransformResult.Failure(InvalidSpatialReferenceMessage);
        }
    }

    private static async Task<SkiaMapRenderer.RenderExtent?> TryTransformExtentWithPostGisAsync(
        IAdoNetDatabaseConnectionProvider connectionProvider,
        ILogger logger,
        SkiaMapRenderer.RenderExtent extent,
        int fromSrid,
        int toSrid,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await connectionProvider
                .OpenConnectionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                WITH points AS (
                    SELECT ST_SetSRID(ST_MakePoint(@minX, @minY), @fromSrid) AS geom
                    UNION ALL
                    SELECT ST_SetSRID(ST_MakePoint(@maxX, @minY), @fromSrid) AS geom
                    UNION ALL
                    SELECT ST_SetSRID(ST_MakePoint(@maxX, @maxY), @fromSrid) AS geom
                    UNION ALL
                    SELECT ST_SetSRID(ST_MakePoint(@minX, @maxY), @fromSrid) AS geom
                ),
                transformed AS (
                    SELECT ST_Transform(geom, @toSrid) AS geom
                    FROM points
                )
                SELECT MIN(ST_X(geom)) AS xmin,
                       MIN(ST_Y(geom)) AS ymin,
                       MAX(ST_X(geom)) AS xmax,
                       MAX(ST_Y(geom)) AS ymax
                FROM transformed
                """;

            AddParameter(command, "@minX", extent.MinX);
            AddParameter(command, "@minY", extent.MinY);
            AddParameter(command, "@maxX", extent.MaxX);
            AddParameter(command, "@maxY", extent.MaxY);
            AddParameter(command, "@fromSrid", fromSrid);
            AddParameter(command, "@toSrid", toSrid);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            if (reader.IsDBNull(0) || reader.IsDBNull(1) || reader.IsDBNull(2) || reader.IsDBNull(3))
            {
                return null;
            }

            return new SkiaMapRenderer.RenderExtent(
                reader.GetDouble(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetDouble(3));
        }
        catch (OperationCanceledException)
        {
            // Request cancelled or timed out — propagate rather than masking as 400.
            throw;
        }
        // Intentional: extent transform is a best-effort optimization for the render
        // pipeline; a provider/query failure falls back to null (caller computes the extent
        // another way) rather than failing the render.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            RasterMapRenderingPipelineLog.PostGisExtentTransformFailed(logger, fromSrid, toSrid, ex);
            return null;
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal readonly record struct ExtentTransformResult(
        bool IsSuccess,
        SkiaMapRenderer.RenderExtent Extent,
        string? Error)
    {
        public static ExtentTransformResult Success(SkiaMapRenderer.RenderExtent extent)
            => new(true, extent, null);

        public static ExtentTransformResult Failure(string error)
            => new(false, default, error);
    }

    internal readonly record struct RasterTileRenderResult(
        bool IsSuccess,
        byte[] ImageBytes,
        int FeatureCount,
        IResult? Error)
    {
        public static RasterTileRenderResult Success(byte[] imageBytes, int featureCount)
            => new(true, imageBytes, featureCount, null);

        public static RasterTileRenderResult Failure(IResult error)
            => new(false, [], 0, error);
    }

    internal static SpatialFilter CreateBboxSpatialFilter(SkiaMapRenderer.RenderExtent extent, int srid)
        => SpatialFilterHelpers.CreateBboxSpatialFilter(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, srid);

    internal static SKColor? ResolveBackgroundColor(string? backgroundColor)
    {
        if (string.IsNullOrWhiteSpace(backgroundColor) ||
            backgroundColor.Length != 8 ||
            !backgroundColor.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
            !uint.TryParse(
                backgroundColor.AsSpan(2),
                System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture,
                out var rgb))
        {
            return null;
        }

        return new SKColor(
            (byte)((rgb >> 16) & 0xff),
            (byte)((rgb >> 8) & 0xff),
            (byte)(rgb & 0xff));
    }

    private static bool EvaluateFilter(MapLibreExpression filter, System.Collections.Immutable.ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var result = ExpressionEvaluator.Evaluate(filter, properties, zoom);
        return result is bool b && b;
    }
}
