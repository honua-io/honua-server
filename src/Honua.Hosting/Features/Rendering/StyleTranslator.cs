// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using Honua.Core.Features.Metadata.Domain.V2;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

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
    /// Returns whether any of a layer's filter, paint, or layout expressions read <c>["zoom"]</c>,
    /// and so resolve to different values at different zooms.
    /// </summary>
    /// <remarks>
    /// Callers use this to keep zoom-dependent styles out of caches and precomputes that are not
    /// keyed by zoom. Unlike feature attributes — which vary per feature and are detected by
    /// <see cref="CollectReferencedFields(MapLibreStyleLayer[])"/> — zoom is constant across a single render but varies
    /// between renders, so a style value resolved once and reused across requests silently freezes
    /// at whatever zoom happened to resolve it first. The walk deliberately errs toward reporting
    /// <see langword="true"/> (it descends into <c>literal</c> arrays as well): a false positive only
    /// gives up a fast path, whereas a false negative reinstates the stale-value bug.
    /// </remarks>
    public static bool UsesZoomExpression(MapLibreStyleLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);

        return ContainsZoomExpression(layer.Filter) ||
               ContainsZoomExpression(layer.Paint) ||
               ContainsZoomExpression(layer.Layout);
    }

    private static bool ContainsZoomExpression(Dictionary<string, MapLibreExpression>? expressions)
    {
        if (expressions == null || expressions.Count == 0)
        {
            return false;
        }

        return expressions.Values.Any(ContainsZoomExpression);
    }

    private static bool ContainsZoomExpression(MapLibreExpression? expression)
    {
        if (!expression.HasValue ||
            expression.Value.Kind != MapLibreExpressionKind.Array ||
            expression.Value.Items is not { Length: > 0 } items)
        {
            return false;
        }

        if (TryGetString(items[0], out var op) &&
            string.Equals(op, "zoom", StringComparison.Ordinal))
        {
            return true;
        }

        return items.Any(ContainsZoomExpression);
    }

    /// <summary>
    /// Returns whether a style layer should be rendered at the supplied <paramref name="zoom"/>.
    /// Layer visibility is always honored; minzoom/maxzoom apply when the render carries a derived
    /// zoom and are left unapplied when <see cref="RenderZoom.NotDerivable"/> states that no zoom
    /// could be derived for the request.
    /// </summary>
    public static bool ShouldRenderLayer(
        MapLibreStyleLayer layer,
        RenderZoom zoom,
        ImmutableDictionary<string, object?>? properties = null)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        if (zoom.Level is { } level && !IsLayerInZoomRange(layer, level))
        {
            return false;
        }

        return IsLayerVisible(layer, properties ?? ImmutableDictionary<string, object?>.Empty, zoom);
    }

    /// <summary>
    /// Returns whether any layer in a style document falls inside its minzoom/maxzoom range at the
    /// supplied <paramref name="zoom"/>. Callers use this to skip querying features for a layer
    /// whose style is entirely out of range, since none of it could be drawn. Styles with no layers
    /// render with default paints and are always in range.
    /// </summary>
    public static bool IsAnyLayerInZoomRange(MapLibreStyleLayer[] styleLayers, RenderZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(styleLayers);
        ArgumentNullException.ThrowIfNull(zoom);

        if (zoom.Level is not { } level || styleLayers.Length == 0)
        {
            return true;
        }

        return styleLayers.Any(styleLayer => IsLayerInZoomRange(styleLayer, level));
    }

    /// <summary>
    /// Resolves a fill style from a MapLibre layer for a specific feature at the supplied
    /// <paramref name="zoom"/>, which zoom-dependent paint expressions are evaluated against.
    /// </summary>
    public static ResolvedFillStyle ResolveFillStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        var paint = layer.Paint;
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedFillStyle
            {
                FillColor = new SKColor(45, 105, 165, 64),
                OutlineColor = new SKColor(45, 105, 165, 255)
            };
        }

        var fillColor = ResolveColor(paint, "fill-color", properties, zoom, new SKColor(0, 0, 0));
        var fillOpacity = ResolveFloat(paint, "fill-opacity", properties, zoom, 1f);
        var outlineColor = ResolveOptionalColor(paint, "fill-outline-color", properties, zoom);
        var antialias = ResolveBool(paint, "fill-antialias", properties, zoom, true);

        fillColor = ApplyOpacity(fillColor, fillOpacity);

        return new ResolvedFillStyle
        {
            FillColor = fillColor,
            OutlineColor = outlineColor,
            OutlineWidth = 1f,
            Antialias = antialias
        };
    }

    /// <summary>
    /// Resolves a line style from a MapLibre layer for a specific feature at the supplied
    /// <paramref name="zoom"/>, which zoom-dependent paint expressions are evaluated against.
    /// </summary>
    public static ResolvedLineStyle ResolveLineStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        var paint = layer.Paint;
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedLineStyle
            {
                LineColor = new SKColor(45, 105, 165, 255),
                LineWidth = 2f
            };
        }

        var lineColor = ResolveColor(paint, "line-color", properties, zoom, SKColors.Black);
        var lineWidth = ResolveFloat(paint, "line-width", properties, zoom, 1f);
        var lineOpacity = ResolveFloat(paint, "line-opacity", properties, zoom, 1f);

        float[]? dashArray = ResolveFloatArray(paint, "line-dasharray");

        var lineCap = SKStrokeCap.Butt;
        var lineJoin = SKStrokeJoin.Miter;

        var layout = layer.Layout;
        if (layout != null && layout.Count > 0)
        {
            var cap = ResolveString(layout, "line-cap", properties, zoom);
            if (cap != null)
            {
                lineCap = cap switch
                {
                    "round" => SKStrokeCap.Round,
                    "square" => SKStrokeCap.Square,
                    _ => SKStrokeCap.Butt
                };
            }

            var join = ResolveString(layout, "line-join", properties, zoom);
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

        lineColor = ApplyOpacity(lineColor, lineOpacity);

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
    /// Resolves a circle style from a MapLibre layer for a specific feature at the supplied
    /// <paramref name="zoom"/>, which zoom-dependent paint expressions are evaluated against.
    /// </summary>
    public static ResolvedCircleStyle ResolveCircleStyle(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        var paint = layer.Paint;
        if (paint == null || paint.Count == 0)
        {
            return new ResolvedCircleStyle
            {
                Radius = 5f,
                FillColor = new SKColor(45, 105, 165, 255)
            };
        }

        var radius = ResolveFloat(paint, "circle-radius", properties, zoom, 5f);
        var fillColor = ResolveColor(paint, "circle-color", properties, zoom, SKColors.Black);
        var fillOpacity = ResolveFloat(paint, "circle-opacity", properties, zoom, 1f);
        var strokeColor = ResolveOptionalColor(paint, "circle-stroke-color", properties, zoom);
        var strokeOpacity = ResolveFloat(paint, "circle-stroke-opacity", properties, zoom, 1f);
        var strokeWidth = ResolveFloat(paint, "circle-stroke-width", properties, zoom, 0f);

        fillColor = ApplyOpacity(fillColor, fillOpacity);

        if (strokeColor.HasValue)
        {
            strokeColor = ApplyOpacity(strokeColor.Value, strokeOpacity);
        }

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
        MetadataV2GeometryType geometryType)
    {
        var strokeColor = new SKColor(45, 105, 165, 255);
        var fillColor = new SKColor(45, 105, 165, 64);

        return geometryType switch
        {
            MetadataV2GeometryType.Point or
            MetadataV2GeometryType.MultiPoint =>
                (new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = 8f,
                    StrokeCap = SKStrokeCap.Round,
                    IsAntialias = true
                }, null),

            MetadataV2GeometryType.LineString or
            MetadataV2GeometryType.MultiLineString =>
                (new SKPaint
                {
                    Style = SKPaintStyle.Stroke,
                    Color = strokeColor,
                    StrokeWidth = 2f,
                    IsAntialias = true,
                    StrokeCap = SKStrokeCap.Round
                }, null),

            MetadataV2GeometryType.Polygon or
            MetadataV2GeometryType.MultiPolygon =>
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

    private static bool IsLayerVisible(
        MapLibreStyleLayer layer,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        if (layer.Layout == null || layer.Layout.Count == 0)
        {
            return true;
        }

        var visibility = ResolveString(layer.Layout, "visibility", properties, zoom);
        return !string.Equals(visibility, "none", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLayerInZoomRange(MapLibreStyleLayer layer, double zoom)
    {
        if (layer.MinZoom.HasValue && zoom < layer.MinZoom.Value)
        {
            return false;
        }

        if (layer.MaxZoom.HasValue && zoom >= layer.MaxZoom.Value)
        {
            return false;
        }

        return true;
    }

    private static SKColor ApplyOpacity(SKColor color, float opacity)
    {
        var alpha = color.Alpha * Math.Clamp(opacity, 0f, 1f);
        return color.WithAlpha((byte)Math.Clamp(alpha, 0f, 255f));
    }

    private static SKColor ResolveColor(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom,
        SKColor defaultColor)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return defaultColor;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => ExpressionEvaluator.ParseColor(expression.StringValue),
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateColor(expression, properties, zoom),
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

        if (TryGetString(items[0], out var op) && op != null &&
            (string.Equals(op, "get", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(op, "has", StringComparison.OrdinalIgnoreCase)) &&
            items.Length > 1 &&
            TryGetString(items[1], out var fieldName) &&
            !string.IsNullOrWhiteSpace(fieldName) &&
            seen.Add(fieldName))
        {
            fields.Add(fieldName);
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
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return null;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => ExpressionEvaluator.ParseColor(expression.StringValue),
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateColor(expression, properties, zoom),
            _ => null
        };
    }

    private static float ResolveFloat(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom,
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
            MapLibreExpressionKind.Array => ExpressionEvaluator.EvaluateFloat(expression, properties, zoom, defaultValue),
            _ => defaultValue
        };
    }

    private static bool ResolveBool(
        Dictionary<string, MapLibreExpression> paint,
        string property,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom,
        bool defaultValue)
    {
        if (!TryGetExpression(paint, property, out var expression))
        {
            return defaultValue;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.Boolean => expression.BoolValue,
            MapLibreExpressionKind.Array => ExpressionEvaluator.Evaluate(expression, properties, zoom) is bool b ? b : defaultValue,
            _ => defaultValue
        };
    }

    private static string? ResolveString(
        Dictionary<string, MapLibreExpression> values,
        string property,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        if (!TryGetExpression(values, property, out var expression))
        {
            return null;
        }

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => expression.StringValue,
            MapLibreExpressionKind.Array => ExpressionEvaluator.Evaluate(expression, properties, zoom)?.ToString(),
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
