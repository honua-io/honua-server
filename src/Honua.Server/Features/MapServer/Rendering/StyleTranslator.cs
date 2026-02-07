// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using SkiaSharp;

namespace Honua.Server.Features.MapServer.Rendering;

/// <summary>
/// Translates MapLibre style JSON into resolved SkiaSharp styles for rendering.
/// </summary>
internal static class StyleTranslator
{
    /// <summary>
    /// Parses MapLibre style JSON into a list of style layers.
    /// </summary>
    public static MapLibreStyleLayer[] ParseStyleLayers(string? mapLibreStyleJson)
    {
        if (string.IsNullOrWhiteSpace(mapLibreStyleJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(mapLibreStyleJson);
            var root = doc.RootElement;

            // Style can be a full document or just an array of layers
            if (root.ValueKind == JsonValueKind.Array)
            {
                return ParseLayersArray(root);
            }

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("layers", out var layers))
            {
                return ParseLayersArray(layers);
            }

            // Single layer object
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out _))
            {
                var layer = ParseSingleLayer(root);
                return layer != null ? [layer] : [];
            }

            return [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static MapLibreStyleLayer[] ParseLayersArray(JsonElement layersElement)
    {
        if (layersElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var layers = new List<MapLibreStyleLayer>();
        foreach (var element in layersElement.EnumerateArray())
        {
            var layer = ParseSingleLayer(element);
            if (layer != null)
            {
                layers.Add(layer);
            }
        }

        return [.. layers];
    }

    private static MapLibreStyleLayer? ParseSingleLayer(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new MapLibreStyleLayer
        {
            Id = element.TryGetProperty("id", out var id) ? id.GetString() : null,
            Type = element.TryGetProperty("type", out var type) ? type.GetString() : null,
            SourceLayer = element.TryGetProperty("source-layer", out var sl) ? sl.GetString() : null,
            MinZoom = element.TryGetProperty("minzoom", out var minz) && minz.ValueKind == JsonValueKind.Number ? minz.GetDouble() : null,
            MaxZoom = element.TryGetProperty("maxzoom", out var maxz) && maxz.ValueKind == JsonValueKind.Number ? maxz.GetDouble() : null,
            Filter = element.TryGetProperty("filter", out var filter) ? filter.Clone() : null,
            Paint = element.TryGetProperty("paint", out var paint) ? paint.Clone() : null,
            Layout = element.TryGetProperty("layout", out var layout) ? layout.Clone() : null
        };
    }

    /// <summary>
    /// Resolves a fill style from a MapLibre layer for a specific feature.
    /// </summary>
    public static ResolvedFillStyle ResolveFillStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties)
    {
        var paint = layer.Paint;
        if (paint == null || paint.Value.ValueKind != JsonValueKind.Object)
        {
            return new ResolvedFillStyle
            {
                FillColor = new SKColor(45, 105, 165, 64),
                OutlineColor = new SKColor(45, 105, 165, 255)
            };
        }

        var fillColor = ResolveColor(paint.Value, "fill-color", properties, new SKColor(0, 0, 0));
        var fillOpacity = ResolveFloat(paint.Value, "fill-opacity", properties, 1f);
        var outlineColor = ResolveOptionalColor(paint.Value, "fill-outline-color", properties);
        var antialias = ResolveBool(paint.Value, "fill-antialias", true);

        fillColor = fillColor.WithAlpha((byte)Math.Clamp(fillOpacity * 255f, 0f, 255f));

        return new ResolvedFillStyle
        {
            FillColor = fillColor,
            OutlineColor = outlineColor,
            OutlineWidth = 1f,
            Antialias = antialias
        };
    }

    /// <summary>
    /// Resolves a line style from a MapLibre layer for a specific feature.
    /// </summary>
    public static ResolvedLineStyle ResolveLineStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties)
    {
        var paint = layer.Paint;
        if (paint == null || paint.Value.ValueKind != JsonValueKind.Object)
        {
            return new ResolvedLineStyle
            {
                LineColor = new SKColor(45, 105, 165, 255),
                LineWidth = 2f
            };
        }

        var lineColor = ResolveColor(paint.Value, "line-color", properties, SKColors.Black);
        var lineWidth = ResolveFloat(paint.Value, "line-width", properties, 1f);
        var lineOpacity = ResolveFloat(paint.Value, "line-opacity", properties, 1f);

        float[]? dashArray = null;
        if (paint.Value.TryGetProperty("line-dasharray", out var dashElement) &&
            dashElement.ValueKind == JsonValueKind.Array)
        {
            dashArray = ParseFloatArray(dashElement);
        }

        var lineCap = SKStrokeCap.Butt;
        var lineJoin = SKStrokeJoin.Miter;

        if (layer.Layout.HasValue && layer.Layout.Value.ValueKind == JsonValueKind.Object)
        {
            var layoutObj = layer.Layout.Value;
            if (layoutObj.TryGetProperty("line-cap", out var cap))
            {
                lineCap = cap.GetString() switch
                {
                    "round" => SKStrokeCap.Round,
                    "square" => SKStrokeCap.Square,
                    _ => SKStrokeCap.Butt
                };
            }

            if (layoutObj.TryGetProperty("line-join", out var join))
            {
                lineJoin = join.GetString() switch
                {
                    "round" => SKStrokeJoin.Round,
                    "bevel" => SKStrokeJoin.Bevel,
                    _ => SKStrokeJoin.Miter
                };
            }
        }

        lineColor = lineColor.WithAlpha((byte)Math.Clamp(lineOpacity * lineColor.Alpha / 255f * 255f, 0f, 255f));

        return new ResolvedLineStyle
        {
            LineColor = lineColor,
            LineWidth = lineWidth,
            LineOpacity = lineOpacity,
            DashArray = dashArray,
            LineCap = lineCap,
            LineJoin = lineJoin
        };
    }

    /// <summary>
    /// Resolves a circle style from a MapLibre layer for a specific feature.
    /// </summary>
    public static ResolvedCircleStyle ResolveCircleStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties)
    {
        var paint = layer.Paint;
        if (paint == null || paint.Value.ValueKind != JsonValueKind.Object)
        {
            return new ResolvedCircleStyle
            {
                Radius = 5f,
                FillColor = new SKColor(45, 105, 165, 255)
            };
        }

        var radius = ResolveFloat(paint.Value, "circle-radius", properties, 5f);
        var fillColor = ResolveColor(paint.Value, "circle-color", properties, SKColors.Black);
        var fillOpacity = ResolveFloat(paint.Value, "circle-opacity", properties, 1f);
        var strokeColor = ResolveOptionalColor(paint.Value, "circle-stroke-color", properties);
        var strokeWidth = ResolveFloat(paint.Value, "circle-stroke-width", properties, 0f);

        fillColor = fillColor.WithAlpha((byte)Math.Clamp(fillOpacity * 255f, 0f, 255f));

        return new ResolvedCircleStyle
        {
            Radius = radius,
            FillColor = fillColor,
            StrokeColor = strokeColor,
            StrokeWidth = strokeWidth
        };
    }

    /// <summary>
    /// Creates default SkiaSharp paints for a geometry type when no style is defined.
    /// </summary>
    public static (SKPaint fill, SKPaint? stroke) CreateDefaultPaints(
        Honua.Core.Features.Catalog.Domain.GeometryType geometryType)
    {
        var strokeColor = new SKColor(45, 105, 165, 255);
        var fillColor = new SKColor(45, 105, 165, 64);

        return geometryType switch
        {
            Honua.Core.Features.Catalog.Domain.GeometryType.Point or
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPoint =>
                (new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = strokeColor,
                    IsAntialias = true
                }, null),

            Honua.Core.Features.Catalog.Domain.GeometryType.LineString or
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiLineString =>
                (new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = 2f,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round
                }, null),

            Honua.Core.Features.Catalog.Domain.GeometryType.Polygon or
            Honua.Core.Features.Catalog.Domain.GeometryType.MultiPolygon =>
                (new SKPaint
                {
                    Style = SKPaintStyle.Fill,
                    Color = fillColor,
                    IsAntialias = true
                }, new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = 1f,
                    IsAntialias = true
                }),
            _ => (new SKPaint { Style = SKPaintStyle.Fill, Color = fillColor, IsAntialias = true }, null)
        };
    }

    private static SKColor ResolveColor(
        JsonElement paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        SKColor defaultColor)
    {
        if (!paint.TryGetProperty(property, out var element))
        {
            return defaultColor;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ExpressionEvaluator.ParseColor(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return ExpressionEvaluator.EvaluateColor(element, properties);
        }

        return defaultColor;
    }

    private static SKColor? ResolveOptionalColor(
        JsonElement paint,
        string property,
        ImmutableDictionary<string, object?> properties)
    {
        if (!paint.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            return ExpressionEvaluator.ParseColor(element.GetString());
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return ExpressionEvaluator.EvaluateColor(element, properties);
        }

        return null;
    }

    private static float ResolveFloat(
        JsonElement paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        float defaultValue)
    {
        if (!paint.TryGetProperty(property, out var element))
        {
            return defaultValue;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            return (float)element.GetDouble();
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            return ExpressionEvaluator.EvaluateFloat(element, properties, defaultValue);
        }

        return defaultValue;
    }

    private static bool ResolveBool(JsonElement paint, string property, bool defaultValue)
    {
        if (!paint.TryGetProperty(property, out var element))
        {
            return defaultValue;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => defaultValue
        };
    }

    private static float[]? ParseFloatArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = new List<float>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Number)
            {
                values.Add((float)item.GetDouble());
            }
        }

        return values.Count > 0 ? [.. values] : null;
    }
}
