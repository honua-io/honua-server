// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class GeoServicesToMapLibreConverter
{
    public static string Convert(JsonElement drawingInfo, LayerDefinition layer)
    {
        if (layer.GeometryType is GeometryType.None or GeometryType.GeometryCollection)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (!drawingInfo.TryGetProperty("renderer", out var renderer) || renderer.ValueKind != JsonValueKind.Object)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        var rendererType = renderer.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString()
            : null;

        return rendererType switch
        {
            "simple" => ConvertSimpleRenderer(renderer, layer),
            "uniqueValue" => ConvertUniqueValueRenderer(renderer, layer),
            "classBreaks" => ConvertClassBreaksRenderer(renderer, layer),
            _ => StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer))
        };
    }

    private static string ConvertSimpleRenderer(JsonElement renderer, LayerDefinition layer)
    {
        if (!renderer.TryGetProperty("symbol", out var symbol) || symbol.ValueKind != JsonValueKind.Object)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        var sourceId = StyleDefaults.GetSourceId(layer);
        var layers = new List<Dictionary<string, object?>>();

        switch (layer.GeometryType)
        {
            case GeometryType.Point:
            case GeometryType.MultiPoint:
                if (TryGetPictureMarkerPayload(symbol, out var picturePayload))
                {
                    return StyleJsonUtilities.Serialize(BuildPictureMarkerStyle(layer, picturePayload));
                }

                layers.Add(BuildPointLayer(layer, sourceId, symbol));
                break;
            case GeometryType.LineString:
            case GeometryType.MultiLineString:
                layers.Add(BuildLineLayer(layer, sourceId, symbol));
                break;
            case GeometryType.Polygon:
            case GeometryType.MultiPolygon:
                BuildPolygonLayers(layer, sourceId, symbol, layers);
                break;
            default:
                return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        var style = BuildStyle(layer, sourceId, layers);
        return StyleJsonUtilities.Serialize(style);
    }

    private static string ConvertUniqueValueRenderer(JsonElement renderer, LayerDefinition layer)
    {
        var fieldName = TryGetRendererField(renderer, out var field)
            ? field
            : null;

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (!renderer.TryGetProperty("uniqueValueInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (layer.GeometryType is GeometryType.Point or GeometryType.MultiPoint
            && TryBuildPictureMarkerUniqueValueStyle(layer, fieldName, infos, renderer, out var pictureStyle))
        {
            return StyleJsonUtilities.Serialize(pictureStyle);
        }

        var sourceId = StyleDefaults.GetSourceId(layer);
        var expression = new List<object?> { "match", new object?[] { "get", fieldName } };
        Dictionary<string, object?>? outlinePaint = null;
        double? size = null;
        double? lineWidth = null;
        StyleColor? fallbackColor = null;
        var anyValues = false;

        foreach (var info in infos.EnumerateArray())
        {
            if (!info.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            if (!info.TryGetProperty("symbol", out var symbol) || symbol.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetSymbolColor(symbol, out var color))
            {
                continue;
            }

            anyValues = true;
            expression.Add(ConvertValueToken(valueElement));
            expression.Add(color.ToRgbaString());

            if (fallbackColor == null)
            {
                fallbackColor = color;
                size = TryGetNumber(symbol, "size");
                lineWidth = TryGetNumber(symbol, "width");
                outlinePaint = TryBuildOutlinePaint(layer, symbol);
            }
        }

        if (!anyValues)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (renderer.TryGetProperty("defaultSymbol", out var defaultSymbol)
            && defaultSymbol.ValueKind == JsonValueKind.Object
            && TryGetSymbolColor(defaultSymbol, out var defaultColor))
        {
            fallbackColor = defaultColor;
            outlinePaint = TryBuildOutlinePaint(layer, defaultSymbol) ?? outlinePaint;
        }

        expression.Add(fallbackColor?.ToRgbaString() ?? "#2D69A5");

        var lineDashArray = layer.GeometryType is GeometryType.LineString or GeometryType.MultiLineString
            ? GetUniformLineDashArray(infos.EnumerateArray())
            : null;

        var layers = BuildExpressionLayers(layer, sourceId, expression, size, lineWidth, outlinePaint, lineDashArray);
        var style = BuildStyle(layer, sourceId, layers);
        return StyleJsonUtilities.Serialize(style);
    }

    private static string ConvertClassBreaksRenderer(JsonElement renderer, LayerDefinition layer)
    {
        var fieldName = TryGetRendererField(renderer, out var field)
            ? field
            : null;

        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (!renderer.TryGetProperty("classBreakInfos", out var infos) || infos.ValueKind != JsonValueKind.Array)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        if (layer.GeometryType is GeometryType.Point or GeometryType.MultiPoint
            && TryBuildPictureMarkerClassBreakStyle(layer, fieldName, infos, out var pictureStyle))
        {
            return StyleJsonUtilities.Serialize(pictureStyle);
        }

        var breaks = new List<(double MaxValue, StyleColor Color, JsonElement Symbol)>();
        foreach (var info in infos.EnumerateArray())
        {
            if (!info.TryGetProperty("classMaxValue", out var maxElement))
            {
                continue;
            }

            if (!StyleParsingHelpers.TryGetDouble(maxElement, out var maxValue))
            {
                continue;
            }

            if (!info.TryGetProperty("symbol", out var symbol) || symbol.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetSymbolColor(symbol, out var color))
            {
                continue;
            }

            breaks.Add((maxValue, color, symbol));
        }

        if (breaks.Count == 0)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultMapLibreStyle(layer));
        }

        var baseColor = breaks[0].Color;
        var expression = new List<object?> { "step", new object?[] { "get", fieldName }, baseColor.ToRgbaString() };
        for (var i = 1; i < breaks.Count; i++)
        {
            expression.Add(breaks[i - 1].MaxValue);
            expression.Add(breaks[i].Color.ToRgbaString());
        }

        var size = TryGetNumber(breaks[0].Symbol, "size");
        var lineWidth = TryGetNumber(breaks[0].Symbol, "width");
        var outlinePaint = TryBuildOutlinePaint(layer, breaks[0].Symbol);

        var sourceId = StyleDefaults.GetSourceId(layer);
        var lineDashArray = layer.GeometryType is GeometryType.LineString or GeometryType.MultiLineString
            ? GetUniformLineDashArray(breaks.Select(breakInfo => breakInfo.Symbol))
            : null;

        var layers = BuildExpressionLayers(layer, sourceId, expression, size, lineWidth, outlinePaint, lineDashArray);
        var style = BuildStyle(layer, sourceId, layers);
        return StyleJsonUtilities.Serialize(style);
    }

    private static Dictionary<string, object?> BuildStyle(
        LayerDefinition layer,
        string sourceId,
        List<Dictionary<string, object?>> layers)
        => new()
        {
            ["version"] = 8,
            ["name"] = layer.Name,
            ["sources"] = new Dictionary<string, object?>
            {
                [sourceId] = new Dictionary<string, object?>
                {
                    ["type"] = "vector",
                    ["tiles"] = new[] { StyleDefaults.GetTileUrl(layer.Id) },
                    ["minzoom"] = 0,
                    ["maxzoom"] = 22
                }
            },
            ["layers"] = layers
        };

    private static Dictionary<string, object?> BuildPointLayer(LayerDefinition layer, string sourceId, JsonElement symbol)
    {
        _ = TryGetSymbolColor(symbol, out var color);
        var size = TryGetNumber(symbol, "size") ?? StyleDefaults.DefaultPointSize;
        var paint = new Dictionary<string, object?>
        {
            ["circle-color"] = color.ToRgbaString(),
            ["circle-radius"] = size / 2d
        };

        var outline = TryBuildOutlinePaint(layer, symbol);
        if (outline != null)
        {
            if (outline.TryGetValue("line-color", out var strokeColor))
            {
                paint["circle-stroke-color"] = strokeColor;
            }

            if (outline.TryGetValue("line-width", out var strokeWidth))
            {
                paint["circle-stroke-width"] = strokeWidth;
            }
        }

        return BuildLayer(layer, sourceId, "circle", paint, "circle");
    }

    private static Dictionary<string, object?> BuildLineLayer(LayerDefinition layer, string sourceId, JsonElement symbol)
    {
        _ = TryGetSymbolColor(symbol, out var color);
        var width = TryGetNumber(symbol, "width") ?? StyleDefaults.DefaultLineWidth;
        var paint = new Dictionary<string, object?>
        {
            ["line-color"] = color.ToRgbaString(),
            ["line-width"] = width
        };

        if (TryGetLineDashArray(symbol, out var dashArray))
        {
            paint["line-dasharray"] = dashArray;
        }

        return BuildLayer(layer, sourceId, "line", paint, "line");
    }

    private static void BuildPolygonLayers(
        LayerDefinition layer,
        string sourceId,
        JsonElement symbol,
        List<Dictionary<string, object?>> layers)
    {
        _ = TryGetSymbolColor(symbol, out var color);
        var paint = new Dictionary<string, object?>
        {
            ["fill-color"] = color.ToRgbaString()
        };

        if (TryGetFillOpacityOverride(symbol, out var fillOpacity))
        {
            paint["fill-opacity"] = fillOpacity;
        }

        layers.Add(BuildLayer(layer, sourceId, "fill", paint, "fill"));

        var outline = TryBuildOutlinePaint(layer, symbol);
        if (outline != null)
        {
            layers.Add(BuildLayer(layer, sourceId, "outline", outline, "line"));
        }
    }

    private static List<Dictionary<string, object?>> BuildExpressionLayers(
        LayerDefinition layer,
        string sourceId,
        List<object?> expression,
        double? size,
        double? lineWidth,
        Dictionary<string, object?>? outlinePaint,
        double[]? lineDashArray)
    {
        var layers = new List<Dictionary<string, object?>>();

        switch (layer.GeometryType)
        {
            case GeometryType.Point:
            case GeometryType.MultiPoint:
                var pointPaint = new Dictionary<string, object?>
                {
                    ["circle-color"] = expression,
                    ["circle-radius"] = (size ?? StyleDefaults.DefaultPointSize) / 2d
                };

                if (outlinePaint != null)
                {
                    if (outlinePaint.TryGetValue("line-color", out var strokeColor))
                    {
                        pointPaint["circle-stroke-color"] = strokeColor;
                    }

                    if (outlinePaint.TryGetValue("line-width", out var strokeWidth))
                    {
                        pointPaint["circle-stroke-width"] = strokeWidth;
                    }
                }

                layers.Add(BuildLayer(layer, sourceId, "circle", pointPaint, "circle"));
                break;
            case GeometryType.LineString:
            case GeometryType.MultiLineString:
                var linePaint = new Dictionary<string, object?>
                {
                    ["line-color"] = expression,
                    ["line-width"] = lineWidth ?? StyleDefaults.DefaultLineWidth
                };

                if (lineDashArray != null)
                {
                    linePaint["line-dasharray"] = lineDashArray;
                }

                layers.Add(BuildLayer(layer, sourceId, "line", linePaint, "line"));
                break;
            case GeometryType.Polygon:
            case GeometryType.MultiPolygon:
                layers.Add(BuildLayer(layer, sourceId, "fill", new Dictionary<string, object?>
                {
                    ["fill-color"] = expression
                }, "fill"));
                if (outlinePaint != null)
                {
                    layers.Add(BuildLayer(layer, sourceId, "outline", outlinePaint, "line"));
                }
                break;
        }

        return layers;
    }

    private static Dictionary<string, object?> BuildLayer(
        LayerDefinition layer,
        string sourceId,
        string idSuffix,
        Dictionary<string, object?> paint,
        string type)
        => new()
        {
            ["id"] = $"layer-{layer.Id}-{idSuffix}",
            ["type"] = type,
            ["source"] = sourceId,
            ["source-layer"] = StyleDefaults.SourceLayerName,
            ["paint"] = paint
        };

    private static Dictionary<string, object?> BuildSymbolLayer(
        LayerDefinition layer,
        string sourceId,
        Dictionary<string, object?> layout)
        => new()
        {
            ["id"] = $"layer-{layer.Id}-symbol",
            ["type"] = "symbol",
            ["source"] = sourceId,
            ["source-layer"] = StyleDefaults.SourceLayerName,
            ["layout"] = layout
        };

    private static Dictionary<string, object?> BuildPictureMarkerStyle(
        LayerDefinition layer,
        PictureMarkerPayload payload)
    {
        var sourceId = StyleDefaults.GetSourceId(layer);
        var imageId = BuildPictureMarkerId(layer.Id, 0);
        var layout = new Dictionary<string, object?>
        {
            ["icon-image"] = imageId
        };

        if (TryGetUniformPictureMarkerLayout([payload], out var markerLayout))
        {
            ApplyPictureMarkerLayout(layout, markerLayout);
        }

        var layers = new List<Dictionary<string, object?>>
        {
            BuildSymbolLayer(layer, sourceId, layout)
        };

        var style = BuildStyle(layer, sourceId, layers);
        AppendPictureMarkerMetadata(style, [new PictureMarkerImage(imageId, payload)]);
        return style;
    }

    private static bool TryGetRendererField(JsonElement renderer, out string field)
    {
        field = string.Empty;

        if (renderer.TryGetProperty("field1", out var fieldElement) && fieldElement.ValueKind == JsonValueKind.String)
        {
            field = fieldElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(field);
        }

        if (renderer.TryGetProperty("field", out var altField) && altField.ValueKind == JsonValueKind.String)
        {
            field = altField.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(field);
        }

        return false;
    }

    private static bool TryBuildPictureMarkerUniqueValueStyle(
        LayerDefinition layer,
        string fieldName,
        JsonElement infos,
        JsonElement renderer,
        out Dictionary<string, object?> style)
    {
        style = new Dictionary<string, object?>();

        var stops = new List<(object? Value, PictureMarkerPayload Payload)>();
        foreach (var info in infos.EnumerateArray())
        {
            if (!info.TryGetProperty("value", out var valueElement))
            {
                continue;
            }

            if (!info.TryGetProperty("symbol", out var symbol) || symbol.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetPictureMarkerPayload(symbol, out var payload))
            {
                return false;
            }

            stops.Add((ConvertValueToken(valueElement), payload));
        }

        if (stops.Count == 0)
        {
            return false;
        }

        var images = new List<PictureMarkerImage>();
        var expression = new List<object?> { "match", new object?[] { "get", fieldName } };
        var index = 0;

        foreach (var stop in stops)
        {
            var imageId = BuildPictureMarkerId(layer.Id, index++);
            images.Add(new PictureMarkerImage(imageId, stop.Payload));
            expression.Add(stop.Value);
            expression.Add(imageId);
        }

        var fallbackId = images[0].Id;
        if (renderer.TryGetProperty("defaultSymbol", out var defaultSymbol)
            && defaultSymbol.ValueKind == JsonValueKind.Object
            && TryGetPictureMarkerPayload(defaultSymbol, out var defaultPayload))
        {
            fallbackId = BuildPictureMarkerId(layer.Id, index++);
            images.Add(new PictureMarkerImage(fallbackId, defaultPayload));
        }

        expression.Add(fallbackId);

        var layout = new Dictionary<string, object?>
        {
            ["icon-image"] = expression
        };

        if (TryGetUniformPictureMarkerLayout(images.Select(image => image.Payload), out var markerLayout))
        {
            ApplyPictureMarkerLayout(layout, markerLayout);
        }

        var sourceId = StyleDefaults.GetSourceId(layer);
        var layers = new List<Dictionary<string, object?>>
        {
            BuildSymbolLayer(layer, sourceId, layout)
        };

        style = BuildStyle(layer, sourceId, layers);
        AppendPictureMarkerMetadata(style, images);
        return true;
    }

    private static bool TryBuildPictureMarkerClassBreakStyle(
        LayerDefinition layer,
        string fieldName,
        JsonElement infos,
        out Dictionary<string, object?> style)
    {
        style = new Dictionary<string, object?>();

        var stops = new List<(double MaxValue, PictureMarkerPayload Payload)>();
        foreach (var info in infos.EnumerateArray())
        {
            if (!info.TryGetProperty("classMaxValue", out var maxElement))
            {
                continue;
            }

            if (!StyleParsingHelpers.TryGetDouble(maxElement, out var maxValue))
            {
                continue;
            }

            if (!info.TryGetProperty("symbol", out var symbol) || symbol.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetPictureMarkerPayload(symbol, out var payload))
            {
                return false;
            }

            stops.Add((maxValue, payload));
        }

        if (stops.Count == 0)
        {
            return false;
        }

        var images = new List<PictureMarkerImage>();
        var index = 0;
        var baseId = BuildPictureMarkerId(layer.Id, index++);
        images.Add(new PictureMarkerImage(baseId, stops[0].Payload));

        var expression = new List<object?> { "step", new object?[] { "get", fieldName }, baseId };
        for (var i = 1; i < stops.Count; i++)
        {
            var imageId = BuildPictureMarkerId(layer.Id, index++);
            images.Add(new PictureMarkerImage(imageId, stops[i].Payload));
            expression.Add(stops[i - 1].MaxValue);
            expression.Add(imageId);
        }

        var layout = new Dictionary<string, object?>
        {
            ["icon-image"] = expression
        };

        if (TryGetUniformPictureMarkerLayout(images.Select(image => image.Payload), out var markerLayout))
        {
            ApplyPictureMarkerLayout(layout, markerLayout);
        }

        var sourceId = StyleDefaults.GetSourceId(layer);
        var layers = new List<Dictionary<string, object?>>
        {
            BuildSymbolLayer(layer, sourceId, layout)
        };

        style = BuildStyle(layer, sourceId, layers);
        AppendPictureMarkerMetadata(style, images);
        return true;
    }

    private static bool TryGetSymbolColor(JsonElement symbol, out StyleColor color)
    {
        color = default;

        if (TryGetSymbolStyle(symbol, out var style)
            && EsriStyleMappings.IsNullFillStyle(style))
        {
            color = new StyleColor(0, 0, 0, 0);
            return true;
        }

        if (symbol.TryGetProperty("color", out var colorElement)
            && StyleJsonUtilities.TryParseGeoServicesColor(colorElement, out color))
        {
            return true;
        }

        color = new StyleColor(
            (byte)StyleDefaults.DefaultStrokeColor[0],
            (byte)StyleDefaults.DefaultStrokeColor[1],
            (byte)StyleDefaults.DefaultStrokeColor[2],
            (byte)StyleDefaults.DefaultStrokeColor[3]);
        return true;
    }

    private static Dictionary<string, object?>? TryBuildOutlinePaint(LayerDefinition layer, JsonElement symbol)
    {
        if (layer.GeometryType is GeometryType.LineString or GeometryType.MultiLineString)
        {
            return null;
        }

        if (!symbol.TryGetProperty("outline", out var outline) || outline.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!outline.TryGetProperty("color", out var colorElement)
            || !StyleJsonUtilities.TryParseGeoServicesColor(colorElement, out var color))
        {
            return null;
        }

        var width = TryGetNumber(outline, "width") ?? StyleDefaults.DefaultOutlineWidth;
        var paint = new Dictionary<string, object?>
        {
            ["line-color"] = color.ToRgbaString(),
            ["line-width"] = width
        };

        if (TryGetLineDashArray(outline, out var dashArray))
        {
            paint["line-dasharray"] = dashArray;
        }

        return paint;
    }

    private static bool TryGetPictureMarkerPayload(JsonElement symbol, out PictureMarkerPayload payload)
    {
        payload = default;

        if (!TryGetSymbolType(symbol, out var type)
            || !string.Equals(type, "esriPMS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? url = null;
        string? imageData = null;
        string? contentType = null;
        double? width = null;
        double? height = null;
        double? xOffset = null;
        double? yOffset = null;
        double? angle = null;

        if (symbol.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String)
        {
            url = urlElement.GetString();
        }

        if (symbol.TryGetProperty("imageData", out var imageElement) && imageElement.ValueKind == JsonValueKind.String)
        {
            imageData = imageElement.GetString();
        }

        if (symbol.TryGetProperty("contentType", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
        {
            contentType = contentElement.GetString();
        }

        if (TryGetNumber(symbol, "width") is { } widthValue)
        {
            width = widthValue;
        }

        if (TryGetNumber(symbol, "height") is { } heightValue)
        {
            height = heightValue;
        }

        if (TryGetNumber(symbol, "xoffset") is { } xOffsetValue)
        {
            xOffset = xOffsetValue;
        }

        if (TryGetNumber(symbol, "yoffset") is { } yOffsetValue)
        {
            yOffset = yOffsetValue;
        }

        if (TryGetNumber(symbol, "angle") is { } angleValue)
        {
            angle = angleValue;
        }

        if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(imageData))
        {
            return false;
        }

        payload = new PictureMarkerPayload(url, imageData, contentType, width, height, xOffset, yOffset, angle);
        return true;
    }

    private static bool TryGetSymbolType(JsonElement symbol, out string? type)
    {
        type = null;

        if (symbol.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            type = typeElement.GetString();
            return !string.IsNullOrWhiteSpace(type);
        }

        return false;
    }

    private static string BuildPictureMarkerId(int layerId, int index)
        => $"layer-{layerId}-pms-{index}";

    private static void AppendPictureMarkerMetadata(
        Dictionary<string, object?> style,
        IEnumerable<PictureMarkerImage> images)
    {
        if (!style.TryGetValue("metadata", out var metadataObj) || metadataObj is not Dictionary<string, object?> metadata)
        {
            metadata = new Dictionary<string, object?>();
            style["metadata"] = metadata;
        }

        if (!metadata.TryGetValue(StyleMetadataKeys.Images, out var imagesObj)
            || imagesObj is not Dictionary<string, object?> imagesMap)
        {
            imagesMap = new Dictionary<string, object?>();
            metadata[StyleMetadataKeys.Images] = imagesMap;
        }

        foreach (var image in images)
        {
            var entry = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(image.Payload.Url))
            {
                entry["url"] = image.Payload.Url;
            }

            if (!string.IsNullOrWhiteSpace(image.Payload.ImageData))
            {
                entry["imageData"] = image.Payload.ImageData;
            }

            if (!string.IsNullOrWhiteSpace(image.Payload.ContentType))
            {
                entry["contentType"] = image.Payload.ContentType;
            }

            if (image.Payload.Width.HasValue)
            {
                entry["width"] = image.Payload.Width.Value;
            }

            if (image.Payload.Height.HasValue)
            {
                entry["height"] = image.Payload.Height.Value;
            }

            if (image.Payload.XOffset.HasValue)
            {
                entry["xoffset"] = image.Payload.XOffset.Value;
            }

            if (image.Payload.YOffset.HasValue)
            {
                entry["yoffset"] = image.Payload.YOffset.Value;
            }

            if (image.Payload.Angle.HasValue)
            {
                entry["angle"] = image.Payload.Angle.Value;
            }

            imagesMap[image.Id] = entry;
        }
    }

    private static bool TryGetUniformPictureMarkerLayout(
        IEnumerable<PictureMarkerPayload> payloads,
        out PictureMarkerLayout layout)
    {
        layout = default;

        var hasValue = false;
        var baselineSet = false;
        double baselineX = 0d;
        double baselineY = 0d;
        double baselineAngle = 0d;

        foreach (var payload in payloads)
        {
            var x = payload.XOffset ?? 0d;
            var y = payload.YOffset ?? 0d;
            var angle = payload.Angle ?? 0d;

            if (!baselineSet)
            {
                baselineSet = true;
                baselineX = x;
                baselineY = y;
                baselineAngle = angle;
                hasValue = x != 0d || y != 0d || angle != 0d;
                continue;
            }

            if (Math.Abs(x - baselineX) > 0.01d
                || Math.Abs(y - baselineY) > 0.01d
                || Math.Abs(angle - baselineAngle) > 0.01d)
            {
                return false;
            }
        }

        if (!baselineSet || !hasValue)
        {
            return false;
        }

        layout = new PictureMarkerLayout(baselineX, baselineY, baselineAngle);
        return true;
    }

    private static void ApplyPictureMarkerLayout(Dictionary<string, object?> layout, PictureMarkerLayout markerLayout)
    {
        if (markerLayout.XOffset != 0d || markerLayout.YOffset != 0d)
        {
            layout["icon-offset"] = new[] { markerLayout.XOffset, markerLayout.YOffset };
        }

        if (markerLayout.Angle != 0d)
        {
            layout["icon-rotate"] = markerLayout.Angle;
        }
    }

    private readonly record struct PictureMarkerPayload(
        string? Url,
        string? ImageData,
        string? ContentType,
        double? Width,
        double? Height,
        double? XOffset,
        double? YOffset,
        double? Angle);

    private readonly record struct PictureMarkerImage(string Id, PictureMarkerPayload Payload);

    private readonly record struct PictureMarkerLayout(double XOffset, double YOffset, double Angle);

    private static bool TryGetSymbolStyle(JsonElement symbol, out string? style)
    {
        style = null;

        if (symbol.TryGetProperty("style", out var styleElement) && styleElement.ValueKind == JsonValueKind.String)
        {
            style = styleElement.GetString();
            return !string.IsNullOrWhiteSpace(style);
        }

        return false;
    }

    private static bool TryGetLineDashArray(JsonElement symbol, out double[]? dashArray)
    {
        dashArray = null;

        if (!TryGetSymbolStyle(symbol, out var style))
        {
            return false;
        }

        return EsriStyleMappings.TryGetLineDashArray(style, out dashArray);
    }

    private static bool TryGetFillOpacityOverride(JsonElement symbol, out double? fillOpacity)
    {
        fillOpacity = null;

        if (!TryGetSymbolStyle(symbol, out var style))
        {
            return false;
        }

        if (EsriStyleMappings.IsNullFillStyle(style))
        {
            fillOpacity = 0d;
            return true;
        }

        return false;
    }

    private static double[]? GetUniformLineDashArray(IEnumerable<JsonElement> symbols)
    {
        string? baselineStyle = null;
        double[]? baselineDash = null;

        foreach (var symbol in symbols)
        {
            if (symbol.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!TryGetSymbolStyle(symbol, out var style))
            {
                if (baselineStyle != null)
                {
                    continue;
                }

                baselineStyle = EsriStyleMappings.LineStyleSolid;
                continue;
            }

            if (baselineStyle == null)
            {
                baselineStyle = style;
                _ = EsriStyleMappings.TryGetLineDashArray(style, out baselineDash);
                continue;
            }

            if (!string.Equals(style, baselineStyle, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return baselineDash;
    }

    private static double? TryGetNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return StyleParsingHelpers.TryGetDouble(prop, out var value) ? value : null;
    }

    private static object ConvertValueToken(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.ToString() ?? string.Empty
        };
    }
}
