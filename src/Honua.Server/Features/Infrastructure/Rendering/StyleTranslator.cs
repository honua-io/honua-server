// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using SkiaSharp;

namespace Honua.Server.Features.Infrastructure.Rendering;

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

        var trimmed = mapLibreStyleJson.TrimStart();
        if (trimmed.Length == 0)
        {
            return [];
        }

        try
        {
            if (trimmed[0] == '[')
            {
                return JsonSerializer.Deserialize(mapLibreStyleJson, MapLibreStyleJsonContext.Default.MapLibreStyleLayerArray) ?? [];
            }

            if (trimmed[0] == '{')
            {
                var document = JsonSerializer.Deserialize(mapLibreStyleJson, MapLibreStyleJsonContext.Default.MapLibreStyleDocument);
                if (document?.Layers is { Length: > 0 })
                {
                    return document.Layers;
                }

                var layer = JsonSerializer.Deserialize(mapLibreStyleJson, MapLibreStyleJsonContext.Default.MapLibreStyleLayer);
                return layer != null ? [layer] : [];
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return [];
    }

    /// <summary>
    /// Collects attribute names referenced by style filters, paint expressions, and layout expressions.
    /// </summary>
    public static string[] CollectReferencedFields(MapLibreStyleLayer[] styleLayers)
    {
        if (styleLayers.Length == 0)
        {
            return [];
        }

        var fields = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var styleLayer in styleLayers)
        {
            CollectReferencedFields(styleLayer.Filter, fields, seen);
            CollectReferencedFields(styleLayer.Paint, fields, seen);
            CollectReferencedFields(styleLayer.Layout, fields, seen);
        }

        return fields.Count == 0 ? [] : [.. fields];
    }

    /// <summary>
    /// Resolves a fill style from a MapLibre layer for a specific feature.
    /// </summary>
    public static ResolvedFillStyle ResolveFillStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties)
    {
        var paint = layer.Paint;
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedFillStyle
            {
                FillColor = new SKColor(45, 105, 165, 64),
                OutlineColor = new SKColor(45, 105, 165, 255)
            };
        }

        var fillColor = ResolveColor(paint, "fill-color", properties, new SKColor(0, 0, 0));
        var fillOpacity = ResolveFloat(paint, "fill-opacity", properties, 1f);
        var outlineColor = ResolveOptionalColor(paint, "fill-outline-color", properties);
        var antialias = ResolveBool(paint, "fill-antialias", properties, true);

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
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedLineStyle
            {
                LineColor = new SKColor(45, 105, 165, 255),
                LineWidth = 2f
            };
        }

        var lineColor = ResolveColor(paint, "line-color", properties, SKColors.Black);
        var lineWidth = ResolveFloat(paint, "line-width", properties, 1f);
        var lineOpacity = ResolveFloat(paint, "line-opacity", properties, 1f);

        float[]? dashArray = ResolveFloatArray(paint, "line-dasharray");

        var lineCap = SKStrokeCap.Butt;
        var lineJoin = SKStrokeJoin.Miter;

        var layout = layer.Layout;
        if (layout != null && layout.Count > 0)
        {
            var cap = ResolveString(layout, "line-cap", properties);
            if (cap != null)
            {
                lineCap = cap switch
                {
                    "round" => SKStrokeCap.Round,
                    "square" => SKStrokeCap.Square,
                    _ => SKStrokeCap.Butt
                };
            }

            var join = ResolveString(layout, "line-join", properties);
            if (join != null)
            {
                lineJoin = join switch
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
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedCircleStyle
            {
                Radius = 5f,
                FillColor = new SKColor(45, 105, 165, 255)
            };
        }

        var radius = ResolveFloat(paint, "circle-radius", properties, 5f);
        var fillColor = ResolveColor(paint, "circle-color", properties, SKColors.Black);
        var fillOpacity = ResolveFloat(paint, "circle-opacity", properties, 1f);
        var strokeColor = ResolveOptionalColor(paint, "circle-stroke-color", properties);
        var strokeWidth = ResolveFloat(paint, "circle-stroke-width", properties, 0f);

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
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = 8f,
                    StrokeCap = SKStrokeCap.Round,
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
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        SKColor defaultColor)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return defaultColor;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => ExpressionEvaluator.ParseColor(expression.StringValue),
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateColor(expression, properties),
            _ => defaultColor
        };
    }

    private static void CollectReferencedFields(
        Dictionary<string, MapLibreExpression>? expressions,
        List<string> fields,
        HashSet<string> seen)
    {
        if (expressions == null || expressions.Count == 0)
        {
            return;
        }

        foreach (var expression in expressions.Values)
        {
            CollectReferencedFields(expression, fields, seen);
        }
    }

    private static void CollectReferencedFields(
        MapLibreExpression? expression,
        List<string> fields,
        HashSet<string> seen)
    {
        if (!expression.HasValue || expression.Value.Kind != MapLibreExpressionKind.Array || expression.Value.Items is not { Length: > 0 } items)
        {
            return;
        }

        if (TryGetString(items[0], out var op) && op != null)
        {
            if ((string.Equals(op, "get", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(op, "has", StringComparison.OrdinalIgnoreCase)) &&
                items.Length > 1 &&
                TryGetString(items[1], out var fieldName) &&
                !string.IsNullOrWhiteSpace(fieldName) &&
                seen.Add(fieldName))
            {
                fields.Add(fieldName);
            }
        }

        foreach (var item in items)
        {
            CollectReferencedFields(item, fields, seen);
        }
    }

    private static bool TryGetString(MapLibreExpression expression, out string? value)
    {
        value = expression.Kind == MapLibreExpressionKind.String ? expression.StringValue : null;
        return value != null;
    }

    private static SKColor? ResolveOptionalColor(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return null;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => ExpressionEvaluator.ParseColor(expression.StringValue),
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateColor(expression, properties),
            _ => null
        };
    }

    private static float ResolveFloat(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        float defaultValue)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return defaultValue;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.Number => (float)expression.NumberValue,
            MapLibreExpressionKind.String => ExpressionEvaluator.ConvertToFloat(expression.StringValue, defaultValue),
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateFloat(expression, properties, defaultValue),
            _ => defaultValue
        };
    }

    private static bool ResolveBool(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        bool defaultValue)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return defaultValue;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.Boolean => expression.BoolValue,
            MapLibreExpressionKind.Array => ExpressionEvaluator.Evaluate(expression, properties) is bool b ? b : defaultValue,
            _ => defaultValue
        };
    }

    private static string? ResolveString(
        Dictionary<string, MapLibreExpression> values,
        string property,
        ImmutableDictionary<string, object?> properties)
    {
        if (!TryGetExpression(values, property, out var expression))
        {
            return null;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => expression.StringValue,
            MapLibreExpressionKind.Array => ExpressionEvaluator.Evaluate(expression, properties)?.ToString(),
            _ => null
        };
    }

    private static float[]? ResolveFloatArray(
        Dictionary<string, MapLibreExpression> paint,
        string property)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return null;
        }

        return ExpressionEvaluator.TryGetNumberArray(expression, out var values) ? values : null;
    }

    private static bool TryGetExpression(
        Dictionary<string, MapLibreExpression> values,
        string property,
        out MapLibreExpression expression)
    {
        if (values.TryGetValue(property, out expression))
        {
            return true;
        }

        expression = default;
        return false;
    }
}
