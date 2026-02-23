// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Tiles;
using SkiaSharp;

namespace Honua.Server.Features.OgcTiles;

/// <summary>
/// Renders features to a 256x256 PNG raster tile using SkiaSharp.
/// </summary>
internal static class TileRenderer
{
    private const int TileSize = 256;

    /// <summary>
    /// Renders a set of features to a PNG image for the given tile bounds.
    /// </summary>
    /// <param name="features">Features to render.</param>
    /// <param name="bounds">Tile bounding box in map coordinates.</param>
    /// <param name="geometryType">Geometry type of the layer.</param>
    /// <returns>PNG image bytes.</returns>
    internal static byte[] RenderTilePng(
        ImmutableArray<Feature> features,
        TileBounds bounds,
        GeometryType geometryType)
    {
        using var surface = SKSurface.Create(
            new SKImageInfo(TileSize, TileSize, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (features.Length == 0)
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

        using var fill = CreateDefaultFillPaint(geometryType);
        using var stroke = CreateDefaultStrokePaint(geometryType);

        foreach (var feature in features)
        {
            if (feature.Geometry == null || feature.Geometry.Length < 5)
            {
                continue;
            }

            RenderFeature(canvas, feature.Geometry, Transform, fill, stroke, geometryType);
        }

        return EncodePng(surface);
    }

    private static void RenderFeature(
        SKCanvas canvas,
        byte[] wkb,
        Func<double, double, SKPoint> transform,
        SKPaint fill,
        SKPaint? stroke,
        GeometryType geometryType)
    {
        // Minimal WKB reading: byte order (1 byte) + type (4 bytes)
        if (wkb.Length < 5)
        {
            return;
        }

        var wkbType = BitConverter.ToInt32(wkb, 1) & 0xFF; // mask off SRID flags

        switch (wkbType)
        {
            case 1: // Point
                RenderPoint(canvas, wkb, 5, transform, fill);
                break;
            case 2: // LineString
                RenderLineString(canvas, wkb, 5, transform, fill);
                break;
            case 3: // Polygon
                RenderPolygon(canvas, wkb, 5, transform, fill, stroke);
                break;
            case 4: // MultiPoint
                RenderMultiPoint(canvas, wkb, transform, fill);
                break;
            case 5: // MultiLineString
                RenderMultiLineString(canvas, wkb, transform, fill);
                break;
            case 6: // MultiPolygon
                RenderMultiPolygon(canvas, wkb, transform, fill, stroke);
                break;
        }
    }

    private static void RenderPoint(
        SKCanvas canvas, byte[] wkb, int offset,
        Func<double, double, SKPoint> transform, SKPaint fill)
    {
        if (offset + 16 > wkb.Length)
            return;
        var x = BitConverter.ToDouble(wkb, offset);
        var y = BitConverter.ToDouble(wkb, offset + 8);
        var pt = transform(x, y);
        canvas.DrawCircle(pt, 4f, fill);
    }

    private static int RenderLineString(
        SKCanvas canvas, byte[] wkb, int offset,
        Func<double, double, SKPoint> transform, SKPaint paint)
    {
        if (offset + 4 > wkb.Length)
            return offset;
        var count = BitConverter.ToInt32(wkb, offset);
        offset += 4;

        if (count < 2 || offset + count * 16 > wkb.Length)
            return offset + count * 16;

        using var path = new SKPath();
        var first = true;
        for (var i = 0; i < count; i++)
        {
            var x = BitConverter.ToDouble(wkb, offset);
            var y = BitConverter.ToDouble(wkb, offset + 8);
            offset += 16;
            var pt = transform(x, y);
            if (first)
            { path.MoveTo(pt); first = false; }
            else
            { path.LineTo(pt); }
        }

        canvas.DrawPath(path, paint);
        return offset;
    }

    private static int RenderPolygon(
        SKCanvas canvas, byte[] wkb, int offset,
        Func<double, double, SKPoint> transform, SKPaint fill, SKPaint? stroke)
    {
        if (offset + 4 > wkb.Length)
            return offset;
        var ringCount = BitConverter.ToInt32(wkb, offset);
        offset += 4;

        using var path = new SKPath();
        for (var r = 0; r < ringCount; r++)
        {
            if (offset + 4 > wkb.Length)
                break;
            var pointCount = BitConverter.ToInt32(wkb, offset);
            offset += 4;

            if (pointCount < 1 || offset + pointCount * 16 > wkb.Length)
            { offset += pointCount * 16; continue; }

            var first = true;
            for (var i = 0; i < pointCount; i++)
            {
                var x = BitConverter.ToDouble(wkb, offset);
                var y = BitConverter.ToDouble(wkb, offset + 8);
                offset += 16;
                var pt = transform(x, y);
                if (first)
                { path.MoveTo(pt); first = false; }
                else
                { path.LineTo(pt); }
            }

            path.Close();
        }

        canvas.DrawPath(path, fill);
        if (stroke != null)
        {
            canvas.DrawPath(path, stroke);
        }

        return offset;
    }

    private static void RenderMultiPoint(
        SKCanvas canvas, byte[] wkb,
        Func<double, double, SKPoint> transform, SKPaint fill)
    {
        if (wkb.Length < 9)
            return;
        var count = BitConverter.ToInt32(wkb, 5);
        var offset = 9;
        for (var i = 0; i < count; i++)
        {
            if (offset + 21 > wkb.Length)
                break;
            offset += 5; // skip WKB header of sub-geometry
            RenderPoint(canvas, wkb, offset, transform, fill);
            offset += 16;
        }
    }

    private static void RenderMultiLineString(
        SKCanvas canvas, byte[] wkb,
        Func<double, double, SKPoint> transform, SKPaint fill)
    {
        if (wkb.Length < 9)
            return;
        var count = BitConverter.ToInt32(wkb, 5);
        var offset = 9;
        for (var i = 0; i < count; i++)
        {
            if (offset + 5 > wkb.Length)
                break;
            offset += 5; // skip WKB header
            offset = RenderLineString(canvas, wkb, offset, transform, fill);
        }
    }

    private static void RenderMultiPolygon(
        SKCanvas canvas, byte[] wkb,
        Func<double, double, SKPoint> transform, SKPaint fill, SKPaint? stroke)
    {
        if (wkb.Length < 9)
            return;
        var count = BitConverter.ToInt32(wkb, 5);
        var offset = 9;
        for (var i = 0; i < count; i++)
        {
            if (offset + 5 > wkb.Length)
                break;
            offset += 5; // skip WKB header
            offset = RenderPolygon(canvas, wkb, offset, transform, fill, stroke);
        }
    }

    private static SKPaint CreateDefaultFillPaint(GeometryType geometryType) =>
        geometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = new SKColor(65, 105, 225), // Royal blue
                IsAntialias = true
            },
            GeometryType.LineString or GeometryType.MultiLineString => new SKPaint
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

    private static SKPaint? CreateDefaultStrokePaint(GeometryType geometryType) =>
        geometryType is GeometryType.Polygon or GeometryType.MultiPolygon
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
