// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using SkiaSharp;

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Renders features to a raster image using SkiaSharp with MapLibre style translation.
/// </summary>
internal sealed class SkiaMapRenderer : IDisposable
{
    private const float PointGeneralizationPixels = 0.8f;
    private const int PointGeneralizationThreshold = 1024;
    private bool _disposed;

    /// <summary>
    /// Bounding box for the map render.
    /// </summary>
    internal readonly record struct RenderExtent(double MinX, double MinY, double MaxX, double MaxY)
    {
        /// <summary>
        /// Width in map units.
        /// </summary>
        public double Width => MaxX - MinX;

        /// <summary>
        /// Height in map units.
        /// </summary>
        public double Height => MaxY - MinY;
    }

    /// <summary>
    /// Renders features to a PNG image.
    /// </summary>
    /// <param name="features">Features to render</param>
    /// <param name="styleLayers">MapLibre style layers for rendering</param>
    /// <param name="extent">Map extent in map coordinates</param>
    /// <param name="imageWidth">Output image width in pixels</param>
    /// <param name="imageHeight">Output image height in pixels</param>
    /// <param name="transparent">Whether background should be transparent</param>
    /// <param name="backgroundColor">Background color (when not transparent)</param>
    /// <param name="geometryType">Geometry type of the layer</param>
    /// <returns>PNG image bytes</returns>
    internal byte[] RenderMap(
        IReadOnlyList<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        RenderExtent extent,
        int imageWidth,
        int imageHeight,
        bool transparent,
        SKColor? backgroundColor,
        GeometryType geometryType)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        using var surface = SKSurface.Create(new SKImageInfo(imageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;

        // Clear background
        if (transparent)
        {
            canvas.Clear(SKColors.Transparent);
        }
        else
        {
            canvas.Clear(backgroundColor ?? SKColors.White);
        }

        if (features.Count == 0 || (extent.Width <= 0 && extent.Height <= 0))
        {
            return EncodeSurface(surface);
        }

        // Build coordinate transform
        var transform = BuildTransform(extent, imageWidth, imageHeight);

        // Render features with style
        if (styleLayers.Length > 0)
        {
            RenderWithStyles(canvas, features, styleLayers, transform);
        }
        else
        {
            RenderWithDefaults(canvas, features, transform, geometryType);
        }

        return EncodeSurface(surface);
    }

    /// <summary>
    /// Renders a single legend swatch for a style layer.
    /// </summary>
    internal static byte[] RenderLegendSwatch(
        MapLibreStyleLayer styleLayer,
        GeometryType geometryType,
        int width = 20,
        int height = 20)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var emptyProps = ImmutableDictionary<string, object?>.Empty;
        var padding = 2f;

        switch (styleLayer.Type)
        {
            case "fill":
                {
                    var fillStyle = StyleTranslator.ResolveFillStyle(styleLayer, emptyProps);
                    using var fillPaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = fillStyle.FillColor,
                        IsAntialias = fillStyle.Antialias
                    };
                    canvas.DrawRect(padding, padding, width - 2 * padding, height - 2 * padding, fillPaint);

                    if (fillStyle.OutlineColor.HasValue)
                    {
                        using var outlinePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = fillStyle.OutlineColor.Value,
                            StrokeWidth = fillStyle.OutlineWidth,
                            IsAntialias = fillStyle.Antialias
                        };
                        canvas.DrawRect(padding, padding, width - 2 * padding, height - 2 * padding, outlinePaint);
                    }

                    break;
                }
            case "line":
                {
                    var lineStyle = StyleTranslator.ResolveLineStyle(styleLayer, emptyProps);
                    using var linePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Stroke,
                        Color = lineStyle.LineColor,
                        StrokeWidth = Math.Min(lineStyle.LineWidth, height / 4f),
                        StrokeCap = lineStyle.LineCap,
                        StrokeJoin = lineStyle.LineJoin,
                        IsAntialias = true
                    };

                    if (lineStyle.DashArray is { Length: > 0 })
                    {
                        using var dashEffect = SKPathEffect.CreateDash(lineStyle.DashArray, 0);
                        linePaint.PathEffect = dashEffect;
                    }

                    canvas.DrawLine(padding, height / 2f, width - padding, height / 2f, linePaint);
                    break;
                }
            case "circle":
                {
                    var circleStyle = StyleTranslator.ResolveCircleStyle(styleLayer, emptyProps);
                    var radius = Math.Min(circleStyle.Radius, Math.Min(width, height) / 2f - padding);
                    using var circlePaint = new SKPaint
                    {
                        Style = SKPaintStyle.Fill,
                        Color = circleStyle.FillColor,
                        IsAntialias = true
                    };
                    canvas.DrawCircle(width / 2f, height / 2f, radius, circlePaint);

                    if (circleStyle.StrokeColor.HasValue && circleStyle.StrokeWidth > 0)
                    {
                        using var strokePaint = new SKPaint
                        {
                            Style = SKPaintStyle.Stroke,
                            Color = circleStyle.StrokeColor.Value,
                            StrokeWidth = circleStyle.StrokeWidth,
                            IsAntialias = true
                        };
                        canvas.DrawCircle(width / 2f, height / 2f, radius, strokePaint);
                    }

                    break;
                }
            default:
                {
                    // Default swatch based on geometry type
                    RenderDefaultLegendSwatch(canvas, geometryType, width, height, padding);
                    break;
                }
        }

        return EncodeSurface(surface);
    }

    private static void RenderDefaultLegendSwatch(SKCanvas canvas, GeometryType geometryType, int width, int height, float padding)
    {
        var (fill, stroke) = StyleTranslator.CreateDefaultPaints(geometryType);
        try
        {
            switch (geometryType)
            {
                case GeometryType.Point or GeometryType.MultiPoint:
                    canvas.DrawCircle(width / 2f, height / 2f, Math.Min(width, height) / 4f, fill);
                    break;
                case GeometryType.LineString or GeometryType.MultiLineString:
                    canvas.DrawLine(padding, height / 2f, width - padding, height / 2f, fill);
                    break;
                case GeometryType.Polygon or GeometryType.MultiPolygon:
                    canvas.DrawRect(padding, padding, width - 2 * padding, height - 2 * padding, fill);
                    if (stroke != null)
                    {
                        canvas.DrawRect(padding, padding, width - 2 * padding, height - 2 * padding, stroke);
                    }

                    break;
            }
        }
        finally
        {
            fill.Dispose();
            stroke?.Dispose();
        }
    }

    private static void RenderWithStyles(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        MapLibreStyleLayer[] styleLayers,
        Func<double, double, SKPoint> transform)
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

                // Check filter
                if (styleLayer.Filter is { } filter && !EvaluateFilter(filter, feature.Attributes))
                {
                    continue;
                }

                RenderFeatureWithStyle(canvas, feature, styleLayer, transform);
            }
        }
    }

    private static void RenderWithDefaults(
        SKCanvas canvas,
        IReadOnlyList<Feature> features,
        Func<double, double, SKPoint> transform,
        GeometryType geometryType)
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
                Math.Max(PointGeneralizationPixels, circleStyle.Radius * 0.5f));
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

    private static void RenderFeatureWithStyle(
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

    private static bool EvaluateFilter(MapLibreExpression filter, ImmutableDictionary<string, object?> properties)
    {
        var result = ExpressionEvaluator.Evaluate(filter, properties);
        return result switch
        {
            bool b => b,
            _ => true // If filter evaluation fails, include the feature
        };
    }

    /// <summary>
    /// Builds a coordinate transform function from map coordinates to pixel coordinates.
    /// For geographic coordinates (EPSG:4326), Y axis is flipped (lat increases upward but pixels go downward).
    /// For projected coordinates (e.g., EPSG:3857), Y axis is also flipped.
    /// </summary>
    internal static Func<double, double, SKPoint> BuildTransform(RenderExtent extent, int imageWidth, int imageHeight)
    {
        var extentWidth = extent.Width;
        var extentHeight = extent.Height;

        if (extentWidth <= 0 || extentHeight <= 0)
        {
            return (_, _) => new SKPoint(imageWidth / 2f, imageHeight / 2f);
        }

        var scaleX = imageWidth / extentWidth;
        var scaleY = imageHeight / extentHeight;
        var minX = extent.MinX;
        var maxY = extent.MaxY;

        return (x, y) => new SKPoint(
            (float)((x - minX) * scaleX),
            (float)((maxY - y) * scaleY) // Flip Y axis
        );
    }

    private static byte[] EncodeSurface(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Encodes surface to the specified format.
    /// </summary>
    internal static byte[] EncodeSurface(SKSurface surface, string format)
    {
        using var image = surface.Snapshot();
        var imageFormat = format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            "gif" => SKEncodedImageFormat.Gif,
            "webp" => SKEncodedImageFormat.Webp,
            _ => SKEncodedImageFormat.Png
        };

        var quality = imageFormat == SKEncodedImageFormat.Jpeg ? 85 : 100;
        using var data = image.Encode(imageFormat, quality);
        return data.ToArray();
    }

    /// <summary>
    /// Gets the MIME type for an image format string.
    /// </summary>
    internal static string GetContentType(string format)
    {
        return format.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "png8" or "png24" or "png32" => "image/png",
            _ => "image/png"
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
    }
}
