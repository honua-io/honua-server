// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
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

/// <summary>
/// Identifies the tile matrix set (gridset) a raster tile render targets. Controls how the
/// tile envelope coordinates and tile CRS are computed by the shared rendering pipeline.
/// </summary>
internal enum TileGridKind
{
    /// <summary>
    /// OGC WebMercatorQuad gridset (EPSG:3857). One tile covers the world at zoom 0.
    /// </summary>
    WebMercatorQuad = 0,

    /// <summary>
    /// OGC WorldCRS84Quad gridset (CRS84 / EPSG:4326). Two columns by one row at zoom 0.
    /// </summary>
    WorldCrs84Quad = 1
}

/// <summary>
/// An honest, protocol-neutral representation of a requested vertical (elevation)
/// selection on a tile request. Carries the resolved numeric value(s) together with
/// the verbatim token the client sent so adapters can record / telemetry-tag the
/// selection.
/// </summary>
/// <remarks>
/// <para>This value type is shared by the WMTS (<c>elevation=</c>) and OGC API Tiles
/// (<c>subset=Z(...)</c> / <c>subset=elevation(...)</c>) adapters via
/// <c>OgcVerticalSelectionParser</c> so the parse, validation, and recording behavior
/// live in one place (AGENTS.md DRY rule). It is defined here, in the rendering layer,
/// because <see cref="RasterMapRenderingPipeline.RenderLayerDescriptor"/> carries it and
/// the protocol-shared assembly already depends on this assembly (defining it the other
/// way round would introduce a project-reference cycle).</para>
/// <para>An instant selection (single value) sets both <see cref="Min"/> and
/// <see cref="Max"/> to the same value. An interval selection sets them to the
/// low/high bounds. This mirrors the (start, end) shape of the temporal filter so
/// downstream code can treat the two dimensions uniformly.</para>
/// <para>Shape A (#1792) records the selection only — it is not yet bound to a Zarr
/// datacube slice render. That binding is the deferred Shape B follow-up after #1790.</para>
/// </remarks>
internal readonly record struct VerticalSelection(double Min, double Max, string RawValue)
{
    /// <summary>
    /// <see langword="true"/> when the selection is a single value (an instant on the
    /// vertical axis) rather than an interval.
    /// </summary>
    public bool IsInstant => Min.Equals(Max);

    /// <summary>
    /// Creates a single-value (instant) vertical selection from a resolved numeric value.
    /// The <paramref name="rawValue"/> is the verbatim client token, retained for telemetry.
    /// </summary>
    public static VerticalSelection FromValue(double value, string rawValue)
        => new(value, value, rawValue);
}

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

    private const float PointGeneralizationPixels = 0.8f;
    private const int PointGeneralizationThreshold = 1024;
    internal const int TileSize = 256;
    internal const int TileSrid = 3857;
    internal const int GeographicTileSrid = 4326;
    internal const int MaxFeaturesPerLayer = 10_000;
    private const string InvalidSpatialReferenceMessage = "Invalid spatial reference.";
    private static readonly ConcurrentDictionary<int, CachedRasterStylePlan> _rasterStylePlanCache = new();

    /// <summary>
    /// Lightweight descriptor used by <see cref="RenderRasterTileCoreAsync"/> for one render layer.
    /// </summary>
    /// <remarks>
    /// <see cref="VerticalSelection"/> carries an optional elevation/vertical selection
    /// resolved from the WMTS <c>elevation=</c> dimension or the OGC API Tiles
    /// <c>subset=Z(...)</c> axis. In Shape A (#1792) it is recorded/telemetry-only — the
    /// raster pipeline does not yet bind it to a Zarr datacube Z-slice render (that is the
    /// deferred Shape B follow-up after #1790). It is threaded here so the value flows to a
    /// single, honest recording point rather than being silently dropped at the adapter.
    /// </remarks>
    internal readonly record struct RenderLayerDescriptor(
        int LayerId,
        bool HasGeometry,
        MetadataV2GeometryType GeometryType,
        VerticalSelection? VerticalSelection = null);

    internal static RenderLayerDescriptor CreateRenderLayerDescriptorFromV2(
        int layerId,
        bool hasGeometry,
        MetadataV2GeometryType geometryType,
        VerticalSelection? verticalSelection = null)
        => new(layerId, hasGeometry, geometryType, verticalSelection);

    /// <summary>
    /// Renders a raster tile from v2 resource render descriptors using the default
    /// Web Mercator (EPSG:3857, WebMercatorQuad) tile matrix set.
    /// </summary>
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
        => RenderRasterTileCoreAsync(
            context,
            serviceSrid,
            renderLayers,
            TileSrid,
            TileMath.GetTileBounds(x, y, z),
            maxFeatures,
            cancellationToken,
            layerTemporalFilters);

    /// <summary>
    /// Renders a raster tile from v2 resource render descriptors for an explicit tile matrix
    /// set / gridset. Tile envelope coordinates are computed in the gridset's CRS (Web Mercator
    /// for <see cref="TileGridKind.WebMercatorQuad"/>, geographic degrees for
    /// <see cref="TileGridKind.WorldCrs84Quad"/>) and the canonical query pipeline reprojects
    /// into the storage CRS as needed. This is the gridset-aware counterpart to the default
    /// Web Mercator <see cref="RenderRasterTileV2Async(HttpContext, int, IReadOnlyList{RenderLayerDescriptor}, int, int, int, int, CancellationToken, IReadOnlyList{TemporalFilter?})"/>.
    /// </summary>
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
        return RenderRasterTileCoreAsync(
            context,
            serviceSrid,
            renderLayers,
            tileSrid,
            tileBounds,
            maxFeatures,
            cancellationToken,
            layerTemporalFilters);
    }

    /// <summary>
    /// Renders a raster tile for an operator-defined custom tile matrix set described by a
    /// <see cref="GridGeometry"/>. The tile envelope is computed from the grid origin / cell size
    /// (in the gridset CRS) and the canonical query pipeline reprojects from the storage CRS as
    /// needed. The two built-in gridsets continue to flow through the <see cref="TileGridKind"/>
    /// overload so their output stays byte-identical; this overload exists because the enum cannot
    /// represent operator-defined gridsets.
    /// </summary>
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
        return RenderRasterTileCoreAsync(
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
        var renderExtent = new SkiaMapRenderer.RenderExtent(
            tileBounds.XMin,
            tileBounds.YMin,
            tileBounds.XMax,
            tileBounds.YMax);

        await using var renderLease = await context.RequestServices
            .GetRequiredService<RasterRenderCapacityLimiter>()
            .TryAcquireAsync(TileSize, TileSize, cancellationToken)
            .ConfigureAwait(false);
        if (renderLease is null)
        {
            return RasterTileRenderResult.Failure(StandardErrorHelpers.CreateServiceUnavailable(
                context,
                RasterRenderCapacityLimiter.CapacityExceededMessage,
                RasterRenderCapacityLimiter.RetryAfterSeconds));
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
                MetadataV2GeometryType.None);

            return RasterTileRenderResult.Success(emptyImage, 0);
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();

        // The tile envelope is expressed in the gridset CRS (3857 for WebMercatorQuad, 4326 for
        // WorldCRS84Quad). Build the spatial filter in that CRS and let the canonical query
        // pipeline reproject the data from the storage CRS (SpatialReferenceSrid) into the gridset
        // CRS (OutputSrid). This mirrors the OGC API Tiles raster path so no geodesy is duplicated.
        var spatialFilter = CreateBboxSpatialFilter(renderExtent, tileSrid);
        var totalFeatureCount = 0;

        using var surface = SKSurface.Create(new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (surface is null)
        {
            return RasterTileRenderResult.Failure(
                StandardErrorHelpers.CreateInternalServerError(context, "Failed to allocate render surface."));
        }

        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var transform = SkiaMapRenderer.BuildTransform(renderExtent, TileSize, TileSize);

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
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                serviceSrid,
                tileSrid,
                maxFeatures,
                temporalFilter: layerTemporalFilters is { Count: > 0 } && layerIndex < layerTemporalFilters.Count
                    ? layerTemporalFilters[layerIndex]
                    : null);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
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

            var features = await QueryRasterFeaturesAsync(featureReader, layer.LayerId, featureQuery, cancellationToken)
                .ConfigureAwait(false);
            if (features.Length == 0)
            {
                continue;
            }

            totalFeatureCount += features.Length;
            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, layer.GeometryType);
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, "png");
        return RasterTileRenderResult.Success(imageBytes, totalFeatureCount);
    }
    /// <summary>
    /// Result of a single-collection vector render through the Skia pipeline.
    /// </summary>
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

    /// <summary>
    /// Renders a single vector collection to an encoded image using the shared Skia
    /// pipeline and an explicit MapLibre style document. This is the vector counterpart
    /// to the raster-coverage renderer used by OGC API Maps styled-map requests: the
    /// caller resolves the style (e.g. from a styleId-keyed projection) and supplies the
    /// already-validated request extent. Reprojection, capacity limiting, feature
    /// querying, and drawing all reuse the same helpers WMS GetMap and MapServer export
    /// invoke, so no rendering or geodesy logic is duplicated.
    /// </summary>
    /// <param name="context">The current HTTP context (used to resolve request-scoped services).</param>
    /// <param name="layerId">Storage layer identifier for the collection.</param>
    /// <param name="geometryType">Geometry type used to drive default styling and the point fast-path.</param>
    /// <param name="serviceSrid">Storage CRS the feature geometries are stored in.</param>
    /// <param name="mapLibreStyleJson">Resolved MapLibre style JSON to apply, or <c>null</c> for default styling.</param>
    /// <param name="requestExtent">Requested map extent in <paramref name="requestSrid"/>.</param>
    /// <param name="requestSrid">CRS the requested extent (and output image) is expressed in.</param>
    /// <param name="imageWidth">Output image width in pixels.</param>
    /// <param name="imageHeight">Output image height in pixels.</param>
    /// <param name="format">Output image format string (e.g. <c>png</c>, <c>jpeg</c>).</param>
    /// <param name="transparent">Whether the background should be transparent.</param>
    /// <param name="backgroundColor">Background fill used when not transparent (defaults to white).</param>
    /// <param name="temporalFilter">Optional temporal filter for time-enabled collections.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
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
                    RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, geometryType);
                }
            }
        }

        var imageBytes = SkiaMapRenderer.EncodeSurface(surface, format);
        return VectorCollectionRenderResult.Success(imageBytes, totalFeatureCount);
    }

    /// <summary>
    /// Describes one vector layer to composite through
    /// <see cref="RenderBoundStyleVectorLayersAsync"/>. When
    /// <see cref="ExplicitMapLibreStyleJson"/> is <c>null</c> the layer's bound style is
    /// resolved from the shared <see cref="ILayerStyleCatalog"/>; otherwise the supplied
    /// MapLibre document overrides it (the styleId-resolved path).
    /// </summary>
    internal readonly record struct BoundStyleVectorLayer(
        int LayerId,
        MetadataV2GeometryType GeometryType,
        int StorageSrid,
        string? ExplicitMapLibreStyleJson);

    /// <summary>
    /// Composites one or more vector layers onto a single raster image, applying each
    /// layer's bound MapLibre/drawingInfo style (fill/stroke/width/opacity for
    /// polygon/line/point) through the shared Skia draw path — the same
    /// <see cref="StyleTranslator"/> / <see cref="RenderLayerToCanvas"/> primitives WMS
    /// GetMap, MapServer export, and OGC API Maps use. This is the canonical styled
    /// vector→raster helper the vector-aware <see cref="Honua.Core.Features.Raster.Abstractions.IRasterMapRenderer"/>
    /// composite drives so the MCP <c>honua_render_map</c> tool and the OGC/MapServer
    /// surfaces reflect a bound style identically, with no protocol-local rasterization.
    /// Layers draw bottom-to-top in the supplied order.
    /// </summary>
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
                maxFeatures);

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

            RenderLayerToCanvas(canvas, features, stylePlan.StyleLayers, transform, layer.GeometryType);
        }

        return SkiaMapRenderer.EncodeSurface(surface, format);
    }

    internal static void RenderLayerToCanvas(
        SKCanvas canvas,
        ImmutableArray<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        Func<double, double, SKPoint> transform,
        MetadataV2GeometryType geometryType)
    {
        if (styleLayers.Length > 0)
        {
            foreach (var styleLayer in styleLayers)
            {
                if (styleLayer.Type == null)
                {
                    continue;
                }

                if (TryRenderBatchedStyleLayer(canvas, features, styleLayer, transform))
                {
                    continue;
                }

                foreach (var feature in features)
                {
                    if (feature.Geometry == null || feature.Geometry.Length < 5)
                    {
                        continue;
                    }

                    if (styleLayer.Filter is { } filter && !EvaluateFilter(filter, feature.Attributes))
                    {
                        continue;
                    }

                    RenderStyledFeature(canvas, feature, styleLayer, transform);
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
                RenderDefaultPoints(canvas, features, transform, fill);
                return;
            }

            foreach (var feature in features)
            {
                if (feature.Geometry == null || feature.Geometry.Length < 5)
                {
                    continue;
                }

                var result = WkbToSkiaConverter.Convert(feature.Geometry, transform);
                RenderConversionResult(canvas, result, fill, stroke);
            }
        }
    }

    private static void RenderDefaultPoints(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        Func<double, double, SKPoint> transform,
        SKPaint fill)
    {
        if (features.Count == 0)
        {
            return;
        }

        var rented = ArrayPool<SKPoint>.Shared.Rent(features.Count);

        try
        {
            var count = CollectRenderablePoints(features, transform, rented, PointGeneralizationPixels);
            if (count == 0)
            {
                return;
            }

            DrawPointBatch(canvas, rented, count, fill);
        }
        finally
        {
            ArrayPool<SKPoint>.Shared.Return(rented);
        }
    }

    private static bool TryRenderBatchedStyleLayer(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        MapLibreStyleLayer styleLayer,
        Func<double, double, SKPoint> transform)
    {
        if (!string.Equals(styleLayer.Type, "circle", StringComparison.Ordinal) ||
            styleLayer.Filter is not null ||
            StyleTranslator.CollectReferencedFields([styleLayer]).Length != 0)
        {
            return false;
        }

        var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, ImmutableDictionary<string, object?>.Empty);
        RenderCirclePoints(canvas, features, transform, circleStyle);
        return true;
    }

    private static void RenderCirclePoints(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        Func<double, double, SKPoint> transform,
        ResolvedCircleStyle circleStyle)
    {
        if (features.Count == 0 || circleStyle.Radius <= 0)
        {
            return;
        }

        var rented = ArrayPool<SKPoint>.Shared.Rent(features.Count);

        try
        {
            var count = CollectRenderablePoints(
                features,
                transform,
                rented,
                Math.Max(PointGeneralizationPixels, circleStyle.Radius));
            if (count == 0)
            {
                return;
            }

            if (ShouldBatchCirclePoints(features.Count, count))
            {
                using var circlePaint = CreateBatchedCirclePaint(
                    circleStyle.FillColor,
                    Math.Max(1f, circleStyle.Radius * 2f));
                DrawPointBatch(canvas, rented, count, circlePaint);

                if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                {
                    using var strokePaint = CreateBatchedCirclePaint(
                        circleStyle.StrokeColor.Value,
                        Math.Max(1f, (circleStyle.Radius * 2f) + circleStyle.StrokeWidth));
                    DrawPointBatch(canvas, rented, count, strokePaint);
                }
            }
            else
            {
                using var circlePaint = new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = circleStyle.FillColor,
                    IsAntialias = true
                };
                DrawCircleLoop(canvas, rented, count, circleStyle.Radius, circlePaint);

                if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                {
                    using var strokePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = circleStyle.StrokeColor.Value,
                        StrokeWidth = circleStyle.StrokeWidth,
                        IsAntialias = true
                    };
                    DrawCircleLoop(canvas, rented, count, circleStyle.Radius, strokePaint);
                }
            }
        }
        finally
        {
            ArrayPool<SKPoint>.Shared.Return(rented);
        }
    }

    private static bool ShouldBatchCirclePoints(int originalCount, int renderedCount)
        => originalCount >= PointGeneralizationThreshold && (renderedCount * 20) <= (originalCount * 19);

    private static int CollectRenderablePoints(
        IReadOnlyList<Feature> features,
        Func<double, double, SKPoint> transform,
        SKPoint[] rented,
        float generalizationPixels)
    {
        var count = 0;
        var generalize = features.Count >= PointGeneralizationThreshold;
        var seenCells = generalize ? new HashSet<long>() : null;

        foreach (var feature in features)
        {
            if (!WkbToSkiaConverter.TryConvertPoint(feature.Geometry, transform, out var point))
            {
                continue;
            }

            if (seenCells is not null && !seenCells.Add(GetPointCellKey(point, generalizationPixels)))
            {
                continue;
            }

            rented[count++] = point;
        }

        return count;
    }

    private static void DrawPointBatch(
        SKCanvas canvas,
        SKPoint[] rented,
        int count,
        SKPaint paint)
    {
        if (count == rented.Length)
        {
            canvas.DrawPoints(SKPointMode.Points, rented, paint);
            return;
        }

        var points = new SKPoint[count];
        Array.Copy(rented, points, count);
        canvas.DrawPoints(SKPointMode.Points, points, paint);
    }

    private static void DrawCircleLoop(
        SKCanvas canvas,
        SKPoint[] rented,
        int count,
        float radius,
        SKPaint paint)
    {
        for (var i = 0; i < count; i++)
        {
            canvas.DrawCircle(rented[i], radius, paint);
        }
    }

    private static SKPaint CreateBatchedCirclePaint(SKColor color, float diameter)
        => new()
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = diameter,
            Color = color,
            IsAntialias = true
        };

    private static long GetPointCellKey(SKPoint point, float cellSize)
    {
        var cellX = (int)MathF.Floor(point.X / cellSize);
        var cellY = (int)MathF.Floor(point.Y / cellSize);
        return ((long)cellX << 32) | (uint)cellY;
    }

    private static void RenderStyledFeature(
        SKCanvas canvas,
        Feature feature,
        MapLibreStyleLayer styleLayer,
        Func<double, double, SKPoint> transform)
    {
        var result = WkbToSkiaConverter.Convert(feature.Geometry!, transform);
        try
        {
            switch (styleLayer.Type)
            {
                case "fill":
                    {
                        var fillStyle = StyleTranslator.ResolveFillStyle(styleLayer, feature.Attributes);
                        using var fillPaint = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = fillStyle.FillColor,
                            IsAntialias = fillStyle.Antialias
                        };

                        if (result.Path != null)
                        {
                            canvas.DrawPath(result.Path, fillPaint);
                        }

                        if (fillStyle.OutlineColor.HasValue && result.Path != null)
                        {
                            using var outlinePaint = new SKPaint
                            {
                                Style = SKPaintStyle.Stroke,
                                Color = fillStyle.OutlineColor.Value,
                                StrokeWidth = fillStyle.OutlineWidth,
                                IsAntialias = fillStyle.Antialias
                            };
                            canvas.DrawPath(result.Path, outlinePaint);
                        }

                        break;
                    }
                case "line":
                    {
                        var lineStyle = StyleTranslator.ResolveLineStyle(styleLayer, feature.Attributes);
                        using var linePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = lineStyle.LineColor,
                            StrokeWidth = lineStyle.LineWidth,
                            StrokeCap = lineStyle.LineCap,
                            StrokeJoin = lineStyle.LineJoin,
                            IsAntialias = true
                        };

                        if (lineStyle.DashArray is { Length: > 0 })
                        {
                            using var dashEffect = SKPathEffect.CreateDash(lineStyle.DashArray, 0);
                            linePaint.PathEffect = dashEffect;
                        }

                        if (result.Path != null)
                        {
                            canvas.DrawPath(result.Path, linePaint);
                        }

                        break;
                    }
                case "circle":
                    {
                        var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, feature.Attributes);
                        using var circlePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Fill,
                            Color = circleStyle.FillColor,
                            IsAntialias = true
                        };

                        if (result.Points != null)
                        {
                            foreach (var point in result.Points)
                            {
                                canvas.DrawCircle(point, circleStyle.Radius, circlePaint);
                            }

                            if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                            {
                                using var strokePaint = new SKPaint
                                {
                                    Style = SKPaintStyle.Stroke,
                                    Color = circleStyle.StrokeColor.Value,
                                    StrokeWidth = circleStyle.StrokeWidth,
                                    IsAntialias = true
                                };
                                foreach (var point in result.Points)
                                {
                                    canvas.DrawCircle(point, circleStyle.Radius, strokePaint);
                                }
                            }
                        }

                        break;
                    }
            }
        }
        finally
        {
            result.Path?.Dispose();
        }
    }

    private static void RenderConversionResult(
        SKCanvas canvas,
        WkbToSkiaConverter.GeometryConversionResult result,
        SKPaint fill,
        SKPaint? stroke)
    {
        if (result.IsPoint && result.Points != null)
        {
            canvas.DrawPoints(SKPointMode.Points, result.Points, fill);
        }
        else if (result.Path != null)
        {
            canvas.DrawPath(result.Path, fill);
            if (stroke != null)
            {
                canvas.DrawPath(result.Path, stroke);
            }
        }

        result.Path?.Dispose();
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

    /// <summary>
    /// Builds a render-time style plan directly from an explicit MapLibre style JSON
    /// document, bypassing the per-layer style catalog. Used by callers (such as OGC API
    /// Maps styled-map rendering) that resolve the style from an external source — e.g. a
    /// styleId-keyed projection — rather than the layer's stored default style.
    /// </summary>
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

    private static ResolvedCircleStyle? TryResolveSimpleCircleStyle(
        MapLibreStyleLayer[] styleLayers,
        string[] referencedFields)
    {
        if (styleLayers.Length != 1 ||
            !string.Equals(styleLayers[0].Type, "circle", StringComparison.Ordinal) ||
            styleLayers[0].Filter is not null ||
            referencedFields.Length != 0)
        {
            return null;
        }

        var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayers[0], ImmutableDictionary<string, object?>.Empty);
        return circleStyle.Radius > 0 ? circleStyle : null;
    }

    internal static FeatureQuery CreateRasterFeatureQuery(
        RasterStylePlan stylePlan,
        SpatialFilter spatialFilter,
        int spatialReferenceSrid,
        int? outputSrid,
        int limit,
        SqlFragment? sqlFilter = null,
        TemporalFilter? temporalFilter = null)
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
        var generalizationPixels = Math.Max(PointGeneralizationPixels, circleStyle.Radius);
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

        if (points.Length >= PointGeneralizationThreshold)
        {
            using var circlePaint = CreateBatchedCirclePaint(
                circleStyle.FillColor,
                Math.Max(1f, circleStyle.Radius * 2f));
            DrawPointBatch(canvas, projectedPoints, points.Length, circlePaint);

            if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
            {
                using var strokePaint = CreateBatchedCirclePaint(
                    circleStyle.StrokeColor.Value,
                    Math.Max(1f, (circleStyle.Radius * 2f) + circleStyle.StrokeWidth));
                DrawPointBatch(canvas, projectedPoints, points.Length, strokePaint);
            }

            return;
        }

        using var fillPaint = new SKPaint
        {
            Style = SKPaintStyle.Fill,
            Color = circleStyle.FillColor,
            IsAntialias = true
        };
        DrawCircleLoop(canvas, projectedPoints, points.Length, circleStyle.Radius, fillPaint);

        if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
        {
            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = circleStyle.StrokeColor.Value,
                StrokeWidth = circleStyle.StrokeWidth,
                IsAntialias = true
            };
            DrawCircleLoop(canvas, projectedPoints, points.Length, circleStyle.Radius, strokePaint);
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
        catch (Exception ex)
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

    private static bool EvaluateFilter(MapLibreExpression filter, System.Collections.Immutable.ImmutableDictionary<string, object?> properties)
    {
        var result = ExpressionEvaluator.Evaluate(filter, properties);
        return result is bool b && b;
    }
}
