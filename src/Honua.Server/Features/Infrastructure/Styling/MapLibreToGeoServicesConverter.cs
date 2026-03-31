// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class MapLibreToGeoServicesConverter
{
    public static string Convert(string mapLibreStyleJson, LayerDefinition layer)
    {
        if (string.IsNullOrWhiteSpace(mapLibreStyleJson))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }

        if (layer.GeometryType is GeometryType.None)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }

        try
        {
            using var document = JsonDocument.Parse(mapLibreStyleJson);
            var root = document.RootElement;

            if (!root.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
            {
                return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
            }

            if (!TrySelectLayers(layersElement, layer.GeometryType, out var primaryLayer, out var outlineLayer))
            {
                return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
            }

            JsonElement? metadata = root.TryGetProperty("metadata", out var metadataElement)
                ? metadataElement
                : null;

            return BuildDrawingInfo(primaryLayer, outlineLayer, layer, metadata);
        }
        catch (JsonException)
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }
    }

    private static string BuildDrawingInfo(
        JsonElement primaryLayer,
        JsonElement? outlineLayer,
        LayerDefinition layer,
        JsonElement? metadata)
    {
        if (layer.GeometryType is GeometryType.Point or GeometryType.MultiPoint
            && TryGetLayerType(primaryLayer, out var layerType)
            && string.Equals(layerType, "symbol", StringComparison.OrdinalIgnoreCase)
            && TryBuildPictureMarkerDrawingInfo(primaryLayer, metadata, out var pictureDrawingInfo))
        {
            return pictureDrawingInfo;
        }

        if (!TryGetPaintProperty(primaryLayer, out var paint))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }

        var colorProperty = layer.GeometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => "circle-color",
            GeometryType.LineString or GeometryType.MultiLineString => "line-color",
            _ => "fill-color"
        };

        if (!paint.TryGetProperty(colorProperty, out var colorElement))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }

        var opacity = GetOpacity(paint, layer.GeometryType);
        var outline = ResolveOutline(paint, outlineLayer, layer.GeometryType);
        var lineWidth = GetLineWidth(paint, layer.GeometryType);
        var size = GetPointSize(paint, layer.GeometryType);
        var lineStyle = ResolveLineStyle(paint, outlineLayer, layer.GeometryType);

        if (colorElement.ValueKind == JsonValueKind.Array)
        {
            if (TryParseMatchExpression(colorElement, out var matchField, out var matchStops, out var fallbackColor))
            {
                return StyleJsonUtilities.Serialize(BuildUniqueValueDrawingInfo(
                    layer.GeometryType,
                    matchField,
                    matchStops,
                    fallbackColor,
                    opacity,
                    outline,
                    lineWidth,
                    size,
                    lineStyle));
            }

            if (TryParseStepExpression(colorElement, out var stepField, out var baseColor, out var stepStops, out var caseFallback))
            {
                return StyleJsonUtilities.Serialize(BuildClassBreakDrawingInfo(
                    layer.GeometryType,
                    stepField,
                    baseColor,
                    stepStops,
                    caseFallback,
                    opacity,
                    outline,
                    lineWidth,
                    size,
                    lineStyle));
            }
        }

        if (!StyleJsonUtilities.TryParseMapLibreColor(colorElement, out var color))
        {
            return StyleJsonUtilities.Serialize(StyleDefaults.BuildDefaultDrawingInfo(layer));
        }

        if (opacity.HasValue)
        {
            color = color.ApplyOpacity(opacity.Value);
        }

        var fillStyle = layer.GeometryType is GeometryType.Polygon or GeometryType.MultiPolygon or GeometryType.GeometryCollection && color.A == 0
            ? EsriStyleMappings.FillStyleNull
            : null;

        var symbol = GeoServicesStyleBuilder.BuildSymbol(layer.GeometryType, color, outline?.Color, outline?.Width, size);
        ApplySymbolStyles(layer.GeometryType, symbol, fillStyle, lineStyle);
        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "simple",
            ["symbol"] = symbol
        };

        return StyleJsonUtilities.Serialize(new Dictionary<string, object?> { ["renderer"] = renderer });
    }

    private static bool TryBuildPictureMarkerDrawingInfo(
        JsonElement primaryLayer,
        JsonElement? metadata,
        out string drawingInfoJson)
    {
        drawingInfoJson = string.Empty;

        if (!TryGetLayerLayout(primaryLayer, out var layout))
        {
            return false;
        }

        if (!layout.TryGetProperty("icon-image", out var iconImage))
        {
            return false;
        }

        var markerLayout = ParsePictureMarkerLayout(layout);

        if (iconImage.ValueKind == JsonValueKind.String)
        {
            var imageId = iconImage.GetString();
            if (string.IsNullOrWhiteSpace(imageId))
            {
                return false;
            }

            if (!TryResolvePictureMarkerPayload(imageId, metadata, out var payload))
            {
                return false;
            }

            var symbol = BuildPictureMarkerSymbol(payload, markerLayout);
            var renderer = new Dictionary<string, object?>
            {
                ["type"] = "simple",
                ["symbol"] = symbol
            };

            drawingInfoJson = StyleJsonUtilities.Serialize(new Dictionary<string, object?> { ["renderer"] = renderer });
            return true;
        }

        if (iconImage.ValueKind == JsonValueKind.Array)
        {
            if (TryParseMatchImageExpression(iconImage, out var matchField, out var matchStops, out var fallbackImage))
            {
                if (TryBuildPictureMarkerUniqueValueRenderer(
                    matchField,
                    matchStops,
                    fallbackImage,
                    metadata,
                    markerLayout,
                    out var renderer))
                {
                    drawingInfoJson = StyleJsonUtilities.Serialize(new Dictionary<string, object?> { ["renderer"] = renderer });
                    return true;
                }

                return false;
            }

            if (TryParseStepImageExpression(iconImage, out var stepField, out var baseImage, out var stepStops))
            {
                if (TryBuildPictureMarkerClassBreakRenderer(
                    stepField,
                    baseImage,
                    stepStops,
                    metadata,
                    markerLayout,
                    out var renderer))
                {
                    drawingInfoJson = StyleJsonUtilities.Serialize(new Dictionary<string, object?> { ["renderer"] = renderer });
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<string, object?> BuildUniqueValueDrawingInfo(
        GeometryType geometryType,
        string field,
        IReadOnlyList<MatchStop> stops,
        StyleColor? fallbackColor,
        double? opacity,
        OutlineStyle? outline,
        double? lineWidth,
        double? size,
        string? lineStyle)
    {
        var uniqueValueInfos = new List<Dictionary<string, object?>>();
        foreach (var stop in stops)
        {
            var color = stop.Color;
            if (opacity.HasValue)
            {
                color = color.ApplyOpacity(opacity.Value);
            }

            var symbol = GeoServicesStyleBuilder.BuildSymbol(
                geometryType,
                color,
                outline?.Color,
                outline?.Width ?? lineWidth,
                size);
            ApplySymbolStyles(geometryType, symbol, null, lineStyle);

            uniqueValueInfos.Add(new Dictionary<string, object?>
            {
                ["value"] = stop.Value,
                ["label"] = stop.Value?.ToString(),
                ["symbol"] = symbol
            });
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "uniqueValue",
            ["field1"] = field,
            ["uniqueValueInfos"] = uniqueValueInfos
        };

        if (fallbackColor.HasValue)
        {
            var color = fallbackColor.Value;
            if (opacity.HasValue)
            {
                color = color.ApplyOpacity(opacity.Value);
            }

            renderer["defaultSymbol"] = GeoServicesStyleBuilder.BuildSymbol(
                geometryType,
                color,
                outline?.Color,
                outline?.Width ?? lineWidth,
                size);
            if (renderer["defaultSymbol"] is Dictionary<string, object?> defaultSymbol)
            {
                ApplySymbolStyles(geometryType, defaultSymbol, null, lineStyle);
            }
        }

        return new Dictionary<string, object?> { ["renderer"] = renderer };
    }

    private static Dictionary<string, object?> BuildClassBreakDrawingInfo(
        GeometryType geometryType,
        string field,
        StyleColor baseColor,
        List<ClassBreakStop> stops,
        StyleColor? caseFallbackColor,
        double? opacity,
        OutlineStyle? outline,
        double? lineWidth,
        double? size,
        string? lineStyle)
    {
        var classBreakInfos = new List<Dictionary<string, object?>>();

        if (stops.Count > 0)
        {
            var firstStop = stops[0];
            var adjustedBase = opacity.HasValue ? baseColor.ApplyOpacity(opacity.Value) : baseColor;
            classBreakInfos.Add(BuildClassBreakInfo(
                geometryType,
                firstStop.Stop,
                adjustedBase,
                outline,
                lineWidth,
                size,
                lineStyle));

            for (var i = 0; i < stops.Count - 1; i++)
            {
                var color = opacity.HasValue ? stops[i].Color.ApplyOpacity(opacity.Value) : stops[i].Color;
                classBreakInfos.Add(BuildClassBreakInfo(
                    geometryType,
                    stops[i + 1].Stop,
                    color,
                    outline,
                    lineWidth,
                    size,
                    lineStyle));
            }

            var lastColor = opacity.HasValue ? stops[^1].Color.ApplyOpacity(opacity.Value) : stops[^1].Color;
            classBreakInfos.Add(BuildClassBreakInfo(
                geometryType,
                double.MaxValue,
                lastColor,
                outline,
                lineWidth,
                size,
                lineStyle));
        }

        var renderer = new Dictionary<string, object?>
        {
            ["type"] = "classBreaks",
            ["field"] = field,
            ["classBreakInfos"] = classBreakInfos
        };

        // Emit defaultSymbol from the case fallback color so that features with
        // null/missing/non-castable values render with the advertised fallback,
        // matching the MapLibre case expression's fallback branch.
        if (caseFallbackColor.HasValue)
        {
            var fallback = caseFallbackColor.Value;
            if (opacity.HasValue)
                fallback = fallback.ApplyOpacity(opacity.Value);

            renderer["defaultSymbol"] = GeoServicesStyleBuilder.BuildSymbol(
                geometryType, fallback, outline?.Color, outline?.Width ?? lineWidth, size);
            if (renderer["defaultSymbol"] is Dictionary<string, object?> defaultSymbol)
                ApplySymbolStyles(geometryType, defaultSymbol, null, lineStyle);
            renderer["defaultLabel"] = "Other";
        }

        return new Dictionary<string, object?> { ["renderer"] = renderer };
    }

    private static Dictionary<string, object?> BuildClassBreakInfo(
        GeometryType geometryType,
        double maxValue,
        StyleColor color,
        OutlineStyle? outline,
        double? lineWidth,
        double? size,
        string? lineStyle)
    {
        var symbol = GeoServicesStyleBuilder.BuildSymbol(
            geometryType,
            color,
            outline?.Color,
            outline?.Width ?? lineWidth,
            size);
        ApplySymbolStyles(geometryType, symbol, null, lineStyle);

        return new Dictionary<string, object?>
        {
            ["classMaxValue"] = maxValue,
            ["label"] = maxValue.ToString(CultureInfo.InvariantCulture),
            ["symbol"] = symbol
        };
    }

    private static bool TrySelectLayers(
        JsonElement layersElement,
        GeometryType geometryType,
        out JsonElement primaryLayer,
        out JsonElement? outlineLayer)
    {
        outlineLayer = null;
        primaryLayer = default;

        switch (geometryType)
        {
            case GeometryType.Point:
            case GeometryType.MultiPoint:
                if (!TryFindLayer(layersElement, "circle", out primaryLayer))
                {
                    return TryFindLayer(layersElement, "symbol", out primaryLayer);
                }
                return true;
            case GeometryType.LineString:
            case GeometryType.MultiLineString:
                return TryFindLayer(layersElement, "line", out primaryLayer);
            case GeometryType.Polygon:
            case GeometryType.MultiPolygon:
            case GeometryType.GeometryCollection:
                if (!TryFindLayer(layersElement, "fill", out primaryLayer))
                {
                    return false;
                }

                outlineLayer = FindOutlineLayer(layersElement, primaryLayer);
                return true;
            default:
                return false;
        }
    }

    private static bool TryFindLayer(JsonElement layersElement, string type, out JsonElement layer)
    {
        foreach (var element in layersElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (element.TryGetProperty("type", out var typeElement)
                && string.Equals(typeElement.GetString(), type, StringComparison.OrdinalIgnoreCase))
            {
                layer = element;
                return true;
            }
        }

        layer = default;
        return false;
    }

    private static bool TryGetLayerType(JsonElement layer, out string? type)
    {
        type = null;

        if (layer.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            type = typeElement.GetString();
            return !string.IsNullOrWhiteSpace(type);
        }

        return false;
    }

    private static bool TryGetLayerLayout(JsonElement layer, out JsonElement layout)
    {
        layout = default;
        return layer.TryGetProperty("layout", out layout) && layout.ValueKind == JsonValueKind.Object;
    }

    private static JsonElement? FindOutlineLayer(JsonElement layersElement, JsonElement fillLayer)
    {
        var source = GetLayerString(fillLayer, "source");
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var sourceLayer = GetLayerString(fillLayer, "source-layer");

        foreach (var element in layersElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!element.TryGetProperty("type", out var typeElement)
                || !string.Equals(typeElement.GetString(), "line", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidateSource = GetLayerString(element, "source");
            if (!string.Equals(candidateSource, source, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(sourceLayer))
            {
                var candidateSourceLayer = GetLayerString(element, "source-layer");
                if (!string.Equals(candidateSourceLayer, sourceLayer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            return element;
        }

        return null;
    }

    private static bool TryGetPaintProperty(JsonElement layer, out JsonElement paint)
    {
        paint = default;
        return layer.TryGetProperty("paint", out paint) && paint.ValueKind == JsonValueKind.Object;
    }

    private static string? GetLayerString(JsonElement layer, string property)
    {
        return layer.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    private static PictureMarkerLayout ParsePictureMarkerLayout(JsonElement layout)
    {
        var iconSize = layout.TryGetProperty("icon-size", out var iconSizeElement)
            && StyleParsingHelpers.TryGetDouble(iconSizeElement, out var sizeValue)
            ? (double?)sizeValue
            : null;

        double? xOffset = null;
        double? yOffset = null;
        if (layout.TryGetProperty("icon-offset", out var offsetElement)
            && offsetElement.ValueKind == JsonValueKind.Array)
        {
            var values = offsetElement.EnumerateArray().ToArray();
            if (values.Length >= 2
                && StyleParsingHelpers.TryGetDouble(values[0], out var xValue)
                && StyleParsingHelpers.TryGetDouble(values[1], out var yValue))
            {
                xOffset = xValue;
                yOffset = yValue;
            }
        }

        var angle = layout.TryGetProperty("icon-rotate", out var rotateElement)
            && StyleParsingHelpers.TryGetDouble(rotateElement, out var rotateValue)
            ? (double?)rotateValue
            : null;

        return new PictureMarkerLayout(iconSize, xOffset, yOffset, angle);
    }

    private static bool TryResolvePictureMarkerPayload(
        string imageId,
        JsonElement? metadata,
        out PictureMarkerPayload payload)
    {
        payload = default;

        if (TryGetPictureMarkerPayloadFromMetadata(imageId, metadata, out payload))
        {
            return true;
        }

        if (TryParseDataUri(imageId, out payload))
        {
            return true;
        }

        if (Uri.TryCreate(imageId, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
        {
            payload = new PictureMarkerPayload(uri.ToString(), null, null, null, null, null, null, null);
            return true;
        }

        return false;
    }

    private static bool TryGetPictureMarkerPayloadFromMetadata(
        string imageId,
        JsonElement? metadata,
        out PictureMarkerPayload payload)
    {
        payload = default;

        if (!metadata.HasValue || metadata.Value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!metadata.Value.TryGetProperty(StyleMetadataKeys.Images, out var imagesElement)
            || imagesElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!imagesElement.TryGetProperty(imageId, out var imageElement)
            || imageElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var url = imageElement.TryGetProperty("url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String
            ? urlElement.GetString()
            : null;
        var imageData = imageElement.TryGetProperty("imageData", out var dataElement) && dataElement.ValueKind == JsonValueKind.String
            ? dataElement.GetString()
            : null;
        var contentType = imageElement.TryGetProperty("contentType", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        double? width = null;
        if (imageElement.TryGetProperty("width", out var widthElement) && StyleParsingHelpers.TryGetDouble(widthElement, out var widthValue))
        {
            width = widthValue;
        }

        double? height = null;
        if (imageElement.TryGetProperty("height", out var heightElement) && StyleParsingHelpers.TryGetDouble(heightElement, out var heightValue))
        {
            height = heightValue;
        }

        double? xOffset = null;
        if (imageElement.TryGetProperty("xoffset", out var xOffsetElement) && StyleParsingHelpers.TryGetDouble(xOffsetElement, out var xOffsetValue))
        {
            xOffset = xOffsetValue;
        }

        double? yOffset = null;
        if (imageElement.TryGetProperty("yoffset", out var yOffsetElement) && StyleParsingHelpers.TryGetDouble(yOffsetElement, out var yOffsetValue))
        {
            yOffset = yOffsetValue;
        }

        double? angle = null;
        if (imageElement.TryGetProperty("angle", out var angleElement) && StyleParsingHelpers.TryGetDouble(angleElement, out var angleValue))
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

    private static bool TryParseDataUri(string input, out PictureMarkerPayload payload)
    {
        payload = default;

        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = input.IndexOf(',');
        if (commaIndex < 0)
        {
            return false;
        }

        var header = input.Substring(5, commaIndex - 5);
        var data = input[(commaIndex + 1)..];
        if (string.IsNullOrWhiteSpace(data))
        {
            return false;
        }

        var contentType = header.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        payload = new PictureMarkerPayload(null, data, contentType, null, null, null, null, null);
        return true;
    }

    private static Dictionary<string, object?> BuildPictureMarkerSymbol(
        PictureMarkerPayload payload,
        PictureMarkerLayout layout)
    {
        var symbol = new Dictionary<string, object?>
        {
            ["type"] = "esriPMS"
        };

        if (!string.IsNullOrWhiteSpace(payload.Url))
        {
            symbol["url"] = payload.Url;
        }

        if (!string.IsNullOrWhiteSpace(payload.ImageData))
        {
            symbol["imageData"] = payload.ImageData;
        }

        if (!string.IsNullOrWhiteSpace(payload.ContentType))
        {
            symbol["contentType"] = payload.ContentType;
        }

        var width = payload.Width;
        var height = payload.Height;
        if (layout.IconSize.HasValue)
        {
            if (width.HasValue)
            {
                width *= layout.IconSize.Value;
            }

            if (height.HasValue)
            {
                height *= layout.IconSize.Value;
            }
        }

        if (width.HasValue)
        {
            symbol["width"] = width.Value;
        }

        if (height.HasValue)
        {
            symbol["height"] = height.Value;
        }

        var xOffset = layout.XOffset ?? payload.XOffset;
        var yOffset = layout.YOffset ?? payload.YOffset;
        var angle = layout.Angle ?? payload.Angle;

        if (xOffset.HasValue)
        {
            symbol["xoffset"] = xOffset.Value;
        }

        if (yOffset.HasValue)
        {
            symbol["yoffset"] = yOffset.Value;
        }

        if (angle.HasValue)
        {
            symbol["angle"] = angle.Value;
        }

        return symbol;
    }

    private static bool TryParseMatchImageExpression(
        JsonElement expression,
        out string field,
        out List<ImageMatchStop> stops,
        out string? fallbackImage)
    {
        field = string.Empty;
        stops = new List<ImageMatchStop>();
        fallbackImage = null;

        if (expression.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = expression.EnumerateArray().ToArray();

        // Unwrap ["case", guard, matchExpr, fallback] null guard
        // emitted by GeoServicesToMapLibreConverter for picture marker unique values.
        if (items.Length == 4
            && items[0].ValueKind == JsonValueKind.String
            && string.Equals(items[0].GetString(), "case", StringComparison.OrdinalIgnoreCase)
            && items[2].ValueKind == JsonValueKind.Array)
        {
            var innerResult = TryParseMatchImageExpression(items[2], out field, out stops, out fallbackImage);
            if (innerResult && items[3].ValueKind == JsonValueKind.String)
                fallbackImage = items[3].GetString();

            return innerResult;
        }

        if (items.Length < 4)
        {
            return false;
        }

        if (items[0].ValueKind != JsonValueKind.String
            || !string.Equals(items[0].GetString(), "match", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseGetExpression(items[1], out field))
        {
            return false;
        }

        for (var i = 2; i < items.Length - 1; i += 2)
        {
            if (i + 1 >= items.Length)
            {
                break;
            }

            if (items[i + 1].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var imageId = items[i + 1].GetString();
            if (string.IsNullOrWhiteSpace(imageId))
            {
                continue;
            }

            stops.Add(new ImageMatchStop(ConvertValue(items[i]), imageId));
        }

        if (items[^1].ValueKind == JsonValueKind.String)
        {
            fallbackImage = items[^1].GetString();
        }

        return stops.Count > 0;
    }

    private static bool TryParseStepImageExpression(
        JsonElement expression,
        out string field,
        out string baseImage,
        out List<ImageClassBreakStop> stops)
    {
        field = string.Empty;
        baseImage = string.Empty;
        stops = new List<ImageClassBreakStop>();

        if (expression.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = expression.EnumerateArray().ToArray();
        if (items.Length < 4)
        {
            return false;
        }

        if (items[0].ValueKind != JsonValueKind.String
            || !string.Equals(items[0].GetString(), "step", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseGetExpression(items[1], out field))
        {
            return false;
        }

        if (items[2].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        baseImage = items[2].GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseImage))
        {
            return false;
        }

        for (var i = 3; i < items.Length - 1; i += 2)
        {
            if (i + 1 >= items.Length)
            {
                break;
            }

            if (!StyleParsingHelpers.TryGetDouble(items[i], out var stop))
            {
                continue;
            }

            if (items[i + 1].ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var imageId = items[i + 1].GetString();
            if (string.IsNullOrWhiteSpace(imageId))
            {
                continue;
            }

            stops.Add(new ImageClassBreakStop(stop, imageId));
        }

        return stops.Count > 0;
    }

    private static bool TryBuildPictureMarkerUniqueValueRenderer(
        string field,
        IReadOnlyList<ImageMatchStop> stops,
        string? fallbackImage,
        JsonElement? metadata,
        PictureMarkerLayout layout,
        out Dictionary<string, object?> renderer)
    {
        renderer = new Dictionary<string, object?>();

        var uniqueValueInfos = new List<Dictionary<string, object?>>();
        foreach (var stop in stops)
        {
            if (!TryResolvePictureMarkerPayload(stop.ImageId, metadata, out var payload))
            {
                continue;
            }

            var symbol = BuildPictureMarkerSymbol(payload, layout);
            uniqueValueInfos.Add(new Dictionary<string, object?>
            {
                ["value"] = stop.Value,
                ["label"] = stop.Value?.ToString(),
                ["symbol"] = symbol
            });
        }

        if (uniqueValueInfos.Count == 0)
        {
            return false;
        }

        renderer["type"] = "uniqueValue";
        renderer["field1"] = field;
        renderer["uniqueValueInfos"] = uniqueValueInfos;

        if (!string.IsNullOrWhiteSpace(fallbackImage)
            && TryResolvePictureMarkerPayload(fallbackImage!, metadata, out var fallbackPayload))
        {
            renderer["defaultSymbol"] = BuildPictureMarkerSymbol(fallbackPayload, layout);
        }

        return true;
    }

    private static bool TryBuildPictureMarkerClassBreakRenderer(
        string field,
        string baseImage,
        IReadOnlyList<ImageClassBreakStop> stops,
        JsonElement? metadata,
        PictureMarkerLayout layout,
        out Dictionary<string, object?> renderer)
    {
        renderer = new Dictionary<string, object?>();

        if (!TryResolvePictureMarkerPayload(baseImage, metadata, out var basePayload))
        {
            return false;
        }

        if (stops.Count == 0)
        {
            return false;
        }

        var classBreakInfos = new List<Dictionary<string, object?>>();
        var baseSymbol = BuildPictureMarkerSymbol(basePayload, layout);
        classBreakInfos.Add(new Dictionary<string, object?>
        {
            ["classMaxValue"] = stops[0].Stop,
            ["label"] = stops[0].Stop.ToString(CultureInfo.InvariantCulture),
            ["symbol"] = baseSymbol
        });

        for (var i = 0; i < stops.Count - 1; i++)
        {
            if (!TryResolvePictureMarkerPayload(stops[i].ImageId, metadata, out var payload))
            {
                return false;
            }

            classBreakInfos.Add(new Dictionary<string, object?>
            {
                ["classMaxValue"] = stops[i + 1].Stop,
                ["label"] = stops[i + 1].Stop.ToString(CultureInfo.InvariantCulture),
                ["symbol"] = BuildPictureMarkerSymbol(payload, layout)
            });
        }

        if (!TryResolvePictureMarkerPayload(stops[^1].ImageId, metadata, out var lastPayload))
        {
            return false;
        }

        classBreakInfos.Add(new Dictionary<string, object?>
        {
            ["classMaxValue"] = double.MaxValue,
            ["label"] = double.MaxValue.ToString(CultureInfo.InvariantCulture),
            ["symbol"] = BuildPictureMarkerSymbol(lastPayload, layout)
        });

        renderer["type"] = "classBreaks";
        renderer["field"] = field;
        renderer["classBreakInfos"] = classBreakInfos;

        return true;
    }

    private static double? GetOpacity(JsonElement paint, GeometryType geometryType)
    {
        var property = geometryType switch
        {
            GeometryType.Point or GeometryType.MultiPoint => "circle-opacity",
            GeometryType.LineString or GeometryType.MultiLineString => "line-opacity",
            _ => "fill-opacity"
        };

        return paint.TryGetProperty(property, out var element) && StyleParsingHelpers.TryGetDouble(element, out var value)
            ? StyleJsonUtilities.ClampOpacity(value)
            : null;
    }

    private static OutlineStyle? ResolveOutline(JsonElement paint, JsonElement? outlineLayer, GeometryType geometryType)
    {
        if (geometryType is GeometryType.Point or GeometryType.MultiPoint)
        {
            if (paint.TryGetProperty("circle-stroke-color", out var strokeColor)
                && StyleJsonUtilities.TryParseMapLibreColor(strokeColor, out var color))
            {
                var width = paint.TryGetProperty("circle-stroke-width", out var widthElement)
                    && StyleParsingHelpers.TryGetDouble(widthElement, out var widthValue)
                    ? widthValue
                    : (double?)StyleDefaults.DefaultOutlineWidth;

                return new OutlineStyle(color, width);
            }

            return null;
        }

        if (geometryType is not (GeometryType.Polygon or GeometryType.MultiPolygon or GeometryType.GeometryCollection))
        {
            return null;
        }

        if (outlineLayer.HasValue && TryGetPaintProperty(outlineLayer.Value, out var outlinePaint))
        {
            if (outlinePaint.TryGetProperty("line-color", out var colorElement)
                && StyleJsonUtilities.TryParseMapLibreColor(colorElement, out var color))
            {
                var width = outlinePaint.TryGetProperty("line-width", out var widthElement)
                    && StyleParsingHelpers.TryGetDouble(widthElement, out var widthValue)
                    ? widthValue
                    : (double?)StyleDefaults.DefaultOutlineWidth;

                var outlineOpacity = outlinePaint.TryGetProperty("line-opacity", out var opacityElement)
                    && StyleParsingHelpers.TryGetDouble(opacityElement, out var opacity)
                    ? (double?)opacity
                    : null;

                if (outlineOpacity.HasValue)
                {
                    color = color.ApplyOpacity(outlineOpacity.Value);
                }

                return new OutlineStyle(color, width);
            }
        }

        if (paint.TryGetProperty("fill-outline-color", out var fillOutline)
            && StyleJsonUtilities.TryParseMapLibreColor(fillOutline, out var fallbackColor))
        {
            return new OutlineStyle(fallbackColor, StyleDefaults.DefaultOutlineWidth);
        }

        return null;
    }

    private static string? ResolveLineStyle(JsonElement paint, JsonElement? outlineLayer, GeometryType geometryType)
    {
        if (geometryType is GeometryType.LineString or GeometryType.MultiLineString)
        {
            return GetLineStyle(paint);
        }

        if (geometryType is GeometryType.Polygon or GeometryType.MultiPolygon or GeometryType.GeometryCollection)
        {
            if (outlineLayer.HasValue && TryGetPaintProperty(outlineLayer.Value, out var outlinePaint))
            {
                return GetLineStyle(outlinePaint);
            }
        }

        return null;
    }

    private static string? GetLineStyle(JsonElement paint)
    {
        if (!TryGetLineDashArray(paint, out var dashArray))
        {
            return null;
        }

        return EsriStyleMappings.TryGetLineStyleFromDashArray(dashArray, out var style) ? style : null;
    }

    private static bool TryGetLineDashArray(JsonElement paint, out double[] dashArray)
    {
        dashArray = Array.Empty<double>();

        if (!paint.TryGetProperty("line-dasharray", out var dashElement)
            || dashElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var values = new List<double>();
        foreach (var item in dashElement.EnumerateArray())
        {
            if (!StyleParsingHelpers.TryGetDouble(item, out var value))
            {
                return false;
            }

            values.Add(value);
        }

        if (values.Count == 0)
        {
            return false;
        }

        dashArray = values.ToArray();
        return true;
    }

    private static void ApplySymbolStyles(
        GeometryType geometryType,
        Dictionary<string, object?> symbol,
        string? fillStyle,
        string? lineStyle)
    {
        if (geometryType is GeometryType.LineString or GeometryType.MultiLineString)
        {
            if (!string.IsNullOrWhiteSpace(lineStyle))
            {
                symbol["style"] = lineStyle;
            }

            return;
        }

        if (geometryType is GeometryType.Polygon or GeometryType.MultiPolygon or GeometryType.GeometryCollection)
        {
            if (!string.IsNullOrWhiteSpace(fillStyle))
            {
                symbol["style"] = fillStyle;
            }

            if (!string.IsNullOrWhiteSpace(lineStyle)
                && symbol.TryGetValue("outline", out var outline)
                && outline is Dictionary<string, object?> outlineSymbol)
            {
                outlineSymbol["style"] = lineStyle;
            }
        }
    }

    private static double? GetLineWidth(JsonElement paint, GeometryType geometryType)
    {
        if (geometryType is not (GeometryType.LineString or GeometryType.MultiLineString))
        {
            return null;
        }

        return paint.TryGetProperty("line-width", out var element) && StyleParsingHelpers.TryGetDouble(element, out var value)
            ? value
            : (double?)StyleDefaults.DefaultLineWidth;
    }

    private static double? GetPointSize(JsonElement paint, GeometryType geometryType)
    {
        if (geometryType is not (GeometryType.Point or GeometryType.MultiPoint))
        {
            return null;
        }

        if (!paint.TryGetProperty("circle-radius", out var element))
        {
            return StyleDefaults.DefaultPointSize;
        }

        return StyleParsingHelpers.TryGetDouble(element, out var value)
            ? value * 2d
            : (double?)StyleDefaults.DefaultPointSize;
    }

    private static bool TryParseMatchExpression(
        JsonElement expression,
        out string field,
        out List<MatchStop> stops,
        out StyleColor? fallbackColor)
    {
        field = string.Empty;
        stops = new List<MatchStop>();
        fallbackColor = null;

        if (expression.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = expression.EnumerateArray().ToArray();

        // Unwrap ["case", guard, matchExpr, fallback] null guard
        // emitted by GeoServicesToMapLibreConverter and StyleSuggestionGenerator.
        // Preserve the fallback color so BuildUniqueValueDrawingInfo can emit defaultSymbol.
        if (items.Length == 4
            && items[0].ValueKind == JsonValueKind.String
            && string.Equals(items[0].GetString(), "case", StringComparison.OrdinalIgnoreCase)
            && items[2].ValueKind == JsonValueKind.Array)
        {
            var innerResult = TryParseMatchExpression(items[2], out field, out stops, out fallbackColor);
            if (innerResult && StyleJsonUtilities.TryParseMapLibreColor(items[3], out var caseFallback))
                fallbackColor = caseFallback;

            return innerResult;
        }

        if (items.Length < 4)
        {
            return false;
        }

        if (!items[0].ValueKind.Equals(JsonValueKind.String)
            || !string.Equals(items[0].GetString(), "match", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseGetExpression(items[1], out field))
        {
            return false;
        }

        for (var i = 2; i < items.Length - 1; i += 2)
        {
            if (i + 1 >= items.Length)
            {
                break;
            }

            if (!StyleJsonUtilities.TryParseMapLibreColor(items[i + 1], out var color))
            {
                continue;
            }

            stops.Add(new MatchStop(ConvertValue(items[i]), color));
        }

        if (StyleJsonUtilities.TryParseMapLibreColor(items[^1], out var fallback))
        {
            fallbackColor = fallback;
        }

        return stops.Count > 0;
    }

    private static bool TryParseStepExpression(
        JsonElement expression,
        out string field,
        out StyleColor baseColor,
        out List<ClassBreakStop> stops,
        out StyleColor? caseFallbackColor)
    {
        field = string.Empty;
        baseColor = default;
        stops = new List<ClassBreakStop>();
        caseFallbackColor = null;

        if (expression.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = expression.EnumerateArray().ToArray();

        // Unwrap ["case", guard, stepExpr, fallback] null guard
        // emitted by GeoServicesToMapLibreConverter and StyleSuggestionGenerator.
        // Preserve the fallback color so BuildClassBreakDrawingInfo can emit defaultSymbol.
        if (items.Length == 4
            && items[0].ValueKind == JsonValueKind.String
            && string.Equals(items[0].GetString(), "case", StringComparison.OrdinalIgnoreCase)
            && items[2].ValueKind == JsonValueKind.Array)
        {
            if (StyleJsonUtilities.TryParseMapLibreColor(items[3], out var fallback))
                caseFallbackColor = fallback;

            return TryParseStepExpression(items[2], out field, out baseColor, out stops, out _);
        }

        if (items.Length < 4)
        {
            return false;
        }

        if (!items[0].ValueKind.Equals(JsonValueKind.String)
            || !string.Equals(items[0].GetString(), "step", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryParseGetExpression(items[1], out field))
        {
            return false;
        }

        if (!StyleJsonUtilities.TryParseMapLibreColor(items[2], out baseColor))
        {
            return false;
        }

        for (var i = 3; i < items.Length - 1; i += 2)
        {
            if (i + 1 >= items.Length)
            {
                break;
            }

            if (!StyleParsingHelpers.TryGetDouble(items[i], out var stop))
            {
                continue;
            }

            if (!StyleJsonUtilities.TryParseMapLibreColor(items[i + 1], out var color))
            {
                continue;
            }

            stops.Add(new ClassBreakStop(stop, color));
        }

        return stops.Count > 0;
    }

    private static bool TryParseGetExpression(JsonElement element, out string field)
        => TryParseGetExpression(element, out field, depth: 0);

    private static bool TryParseGetExpression(JsonElement element, out string field, int depth)
    {
        field = string.Empty;

        if (depth > 5 || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var items = element.EnumerateArray().ToArray();
        if (items.Length < 2)
        {
            return false;
        }

        // Support bare ["get", "field"] and coercion wrappers
        // ["to-string", ["get", "field"]] / ["to-number", ["get", "field"]]
        // emitted by StyleSuggestionGenerator for type-safe match/step expressions.
        var op = items[0].ValueKind == JsonValueKind.String ? items[0].GetString() : null;
        if (string.Equals(op, "to-string", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(op, "to-number", StringComparison.OrdinalIgnoreCase))
        {
            // Unwrap: the inner expression should be ["get", "field"]
            return TryParseGetExpression(items[1], out field, depth + 1);
        }

        if (!string.Equals(op, "get", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (items[1].ValueKind != JsonValueKind.String)
        {
            return false;
        }

        field = items[1].GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(field);
    }

    private static object? ConvertValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out var longValue) ? longValue : element.GetDouble(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.ToString()
        };
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

    private readonly record struct PictureMarkerLayout(
        double? IconSize,
        double? XOffset,
        double? YOffset,
        double? Angle);

    private readonly record struct ImageMatchStop(object? Value, string ImageId);

    private readonly record struct ImageClassBreakStop(double Stop, string ImageId);

    private readonly record struct OutlineStyle(StyleColor Color, double? Width);

    internal readonly record struct MatchStop(object? Value, StyleColor Color);

    internal readonly record struct ClassBreakStop(double Stop, StyleColor Color);
}
