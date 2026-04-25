// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Data.Common;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Domain;
using Honua.Core.Features.Tiles;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Honua.Server.Features.Infrastructure.Rendering;

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
    internal const int MaxFeaturesPerLayer = 10_000;
    private const string InvalidSpatialReferenceMessage = "Invalid spatial reference.";
    private static readonly ConcurrentDictionary<int, CachedRasterStylePlan> _rasterStylePlanCache = new();

    internal static async Task<RasterTileRenderResult> RenderRasterTileAsync(
        HttpContext context,
        ServiceDefinition service,
        IReadOnlyList<LayerDefinition> renderLayers,
        int z,
        int y,
        int x,
        int maxFeatures,
        CancellationToken cancellationToken)
    {
        var tileBounds = TileMath.GetTileBounds(x, y, z);
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
                GeometryType.None);

            return RasterTileRenderResult.Success(emptyImage, 0);
        }

        var featureReader = context.RequestServices.GetRequiredService<IFeatureReader>();
        var styleCatalog = context.RequestServices.GetRequiredService<ILayerStyleCatalog>();
        var spatialFilter = CreateBboxSpatialFilter(renderExtent, TileSrid);
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

        foreach (var layer in renderLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!layer.HasGeometry)
            {
                continue;
            }

            var stylePlan = await GetRasterStylePlanAsync(
                styleCatalog,
                layer.Id,
                cancellationToken).ConfigureAwait(false);
            var featureQuery = CreateRasterFeatureQuery(
                stylePlan,
                spatialFilter,
                service.SpatialReference.Srid,
                TileSrid,
                maxFeatures);

            var renderedPointCount = await TryRenderRasterPointFastPathAsync(
                canvas,
                featureReader,
                layer.Id,
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

            var features = await QueryRasterFeaturesAsync(featureReader, layer.Id, featureQuery, cancellationToken)
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
    internal static void RenderLayerToCanvas(
        SKCanvas canvas,
        ImmutableArray<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        Func<double, double, SKPoint> transform,
        GeometryType geometryType)
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
            try
            {
                if (geometryType == GeometryType.Point)
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
            finally
            {
                fill.Dispose();
                stroke?.Dispose();
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

        result.Path?.Dispose();
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
    {
        var styleLayers = StyleTranslator.ParseStyleLayers(style?.MapLibreStyleJson);
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
        SqlFragment? sqlFilter = null)
    {
        var referencedFields = stylePlan.ReferencedFields;

        return new FeatureQuery
        {
            SpatialFilter = spatialFilter,
            SpatialReferenceSrid = spatialReferenceSrid,
            OutputSrid = outputSrid,
            Limit = limit,
            SqlFilter = sqlFilter,
            OutFields = referencedFields.Length > 0 ? ImmutableArray.CreateRange(referencedFields) : null,
            ExcludeAttributes = referencedFields.Length == 0
        };
    }

    internal static async Task<int> TryRenderRasterPointFastPathAsync(
        SKCanvas canvas,
        IFeatureReader featureReader,
        int layerId,
        GeometryType geometryType,
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
        GeometryType geometryType,
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

        if (geometryType != GeometryType.Point ||
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

        var rented = ArrayPool<SKPoint>.Shared.Rent(points.Length);

        try
        {
            for (var i = 0; i < points.Length; i++)
            {
                rented[i] = transform(points[i].X, points[i].Y);
            }

            if (points.Length >= PointGeneralizationThreshold)
            {
                using var circlePaint = CreateBatchedCirclePaint(
                    circleStyle.FillColor,
                    Math.Max(1f, circleStyle.Radius * 2f));
                DrawPointBatch(canvas, rented, points.Length, circlePaint);

                if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                {
                    using var strokePaint = CreateBatchedCirclePaint(
                        circleStyle.StrokeColor.Value,
                        Math.Max(1f, (circleStyle.Radius * 2f) + circleStyle.StrokeWidth));
                    DrawPointBatch(canvas, rented, points.Length, strokePaint);
                }

                return;
            }

            using var fillPaint = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = circleStyle.FillColor,
                IsAntialias = true
            };
            DrawCircleLoop(canvas, rented, points.Length, circleStyle.Radius, fillPaint);

            if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
            {
                using var strokePaint = new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = circleStyle.StrokeColor.Value,
                    StrokeWidth = circleStyle.StrokeWidth,
                    IsAntialias = true
                };
                DrawCircleLoop(canvas, rented, points.Length, circleStyle.Radius, strokePaint);
            }
        }
        finally
        {
            ArrayPool<SKPoint>.Shared.Return(rented);
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

            var connectionProvider = context.RequestServices.GetService<IDatabaseConnectionProvider>();
            if (connectionProvider == null)
            {
                return ExtentTransformResult.Failure(InvalidSpatialReferenceMessage);
            }

            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Honua.Server.Features.Infrastructure.Rendering.RasterMapRenderingPipeline");

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
        IDatabaseConnectionProvider connectionProvider,
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
