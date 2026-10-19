// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Shared Skia drawing for styled and default feature renders. Map export and
/// raster tile rendering both use these paths so point batching and style paints
/// stay one implementation.
/// </summary>
internal static class SkiaFeatureDrawing
{
    internal const float PointGeneralizationPixels = 0.8f;
    internal const int PointGeneralizationThreshold = 1024;

    internal static void RenderDefaultPoints(
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

    internal static bool TryRenderBatchedStyleLayer(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        MapLibreStyleLayer styleLayer,
        Func<double, double, SKPoint> transform,
        RenderZoom zoom)
    {
        if (!string.Equals(styleLayer.Type, "circle", StringComparison.Ordinal) ||
            styleLayer.Filter is not null ||
            StyleTranslator.CollectReferencedFields([styleLayer]).Length != 0)
        {
            return false;
        }

        var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, ImmutableDictionary<string, object?>.Empty, zoom);
        RenderCirclePoints(canvas, features, transform, circleStyle);
        return true;
    }

    internal static void RenderCirclePoints(
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

    internal static void RenderStyledFeature(
        SKCanvas canvas,
        Feature feature,
        MapLibreStyleLayer styleLayer,
        Func<double, double, SKPoint> transform,
        RenderZoom zoom)
    {
        var result = WkbToSkiaConverter.Convert(feature.Geometry!, transform);
        try
        {
            switch (styleLayer.Type)
            {
                case "fill":
                    {
                        var fillStyle = StyleTranslator.ResolveFillStyle(styleLayer, feature.Attributes, zoom);
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
                        var lineStyle = StyleTranslator.ResolveLineStyle(styleLayer, feature.Attributes, zoom);
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
                        var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, feature.Attributes, zoom);
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

    internal static void RenderConversionResult(
        SKCanvas canvas,
        WkbToSkiaConverter.GeometryConversionResult result,
        SKPaint fill,
        SKPaint? stroke)
    {
        try
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
        }
        finally
        {
            result.Path?.Dispose();
        }
    }

    internal static SKPaint CreateBatchedCirclePaint(SKColor color, float diameter)
        => new()
        {
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = diameter,
            Color = color,
            IsAntialias = true
        };

    internal static void DrawPointBatch(
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

    internal static void DrawCircleLoop(
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

    private static long GetPointCellKey(SKPoint point, float cellSize)
    {
        var cellX = (int)MathF.Floor(point.X / cellSize);
        var cellY = (int)MathF.Floor(point.Y / cellSize);
        return ((long)cellX << 32) | (uint)cellY;
    }
}
