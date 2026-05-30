// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using Honua.Server.Features.Infrastructure.Rendering;
using SkiaSharp;

namespace Honua.Protocols.Ogc.Api.Tiles;

/// <summary>
/// Renders features to a 256x256 PNG raster tile using SkiaSharp.
/// </summary>
internal static class TileRenderer
{
    private const int TileSize = 256;

    internal enum GeometryKind
    {
        Unknown,
        Point,
        MultiPoint,
        LineString,
        MultiLineString,
        Polygon,
        MultiPolygon
    }

    internal readonly record struct TileRenderLayer(ImmutableArray<Feature> Features, GeometryKind GeometryKind);

    /// <summary>
    /// Renders a set of features to a PNG image for the given tile bounds.
    /// </summary>
    /// <param name="features">Features to render.</param>
    /// <param name="bounds">Tile bounding box in map coordinates.</param>
    /// <param name="geometryKind">Geometry kind of the layer.</param>
    /// <returns>PNG image bytes.</returns>
    internal static byte[] RenderTilePng(
        ImmutableArray<Feature> features,
        TileBounds bounds,
        GeometryKind geometryKind)
    {
        return RenderTilePng([new TileRenderLayer(features, geometryKind)], bounds);
    }

    internal static byte[] RenderTilePng(
        IReadOnlyList<TileRenderLayer> layers,
        TileBounds bounds)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (layers.Count == 0)
        {
            return EncodePng(surface);
        }

        var extentWidth = bounds.XMax - bounds.XMin;
        var extentHeight = bounds.YMax - bounds.YMin;

        if (extentWidth <= 0 || extentHeight <= 0)
        {
            return EncodePng(surface);
        }

        var scaleX = TileSize / extentWidth;
        var scaleY = TileSize / extentHeight;
        var minX = bounds.XMin;
        var maxY = bounds.YMax;

        SKPoint Transform(double x, double y) => new(
            (float)((x - minX) * scaleX),
            (float)((maxY - y) * scaleY));

        foreach (var layer in layers)
        {
            using var fill = CreateDefaultFillPaint(layer.GeometryKind);
            using var stroke = CreateDefaultStrokePaint(layer.GeometryKind);

            foreach (var feature in layer.Features)
            {
                if (feature.Geometry == null || feature.Geometry.Length < 5)
                {
                    continue;
                }

                RenderFeature(canvas, feature.Geometry, Transform, fill, stroke, layer.GeometryKind);
            }
        }

        return EncodePng(surface);
    }

    private static void RenderFeature(
        SKCanvas canvas,
        byte[] wkb,
        Func<double, double, SKPoint> transform,
        SKPaint fill,
        SKPaint? stroke,
        GeometryKind geometryKind)
    {
        if (geometryKind is GeometryKind.Point or GeometryKind.MultiPoint &&
            WkbToSkiaConverter.TryConvertPoint(wkb, transform, out var point))
        {
            canvas.DrawCircle(point, 4f, fill);
            return;
        }

        var conversion = WkbToSkiaConverter.Convert(wkb, transform);
        if (conversion.IsPoint && conversion.Points is { Length: > 0 })
        {
            foreach (var convertedPoint in conversion.Points)
            {
                canvas.DrawCircle(convertedPoint, 4f, fill);
            }

            return;
        }

        var path = conversion.Path;
        if (path is null)
        {
            return;
        }

        using (path)
        {
            if (conversion.IsPolygon)
            {
                path.FillType = SKPathFillType.EvenOdd;
            }

            canvas.DrawPath(path, fill);
            if (stroke != null)
            {
                canvas.DrawPath(path, stroke);
            }
        }
    }

    private static SKPaint CreateDefaultFillPaint(GeometryKind geometryKind) =>
        geometryKind switch
        {
            GeometryKind.Point or GeometryKind.MultiPoint => new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(65, 105, 225), // Royal blue
                IsAntialias = true
            },
            GeometryKind.LineString or GeometryKind.MultiLineString => new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(65, 105, 225),
                StrokeWidth = 2f,
                IsAntialias = true
            },
            _ => new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(65, 105, 225, 128),
                IsAntialias = true
            }
        };

    private static SKPaint? CreateDefaultStrokePaint(GeometryKind geometryKind) =>
        geometryKind is GeometryKind.Polygon or GeometryKind.MultiPolygon
            ? new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = new SKColor(0, 0, 139),
                StrokeWidth = 1f,
                IsAntialias = true
            }
            : null;

    private static byte[] EncodePng(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
