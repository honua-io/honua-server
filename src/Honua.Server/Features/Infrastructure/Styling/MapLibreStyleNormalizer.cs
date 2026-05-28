// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class MapLibreStyleNormalizer
{
    private const int MinSupportedZoom = 0;
    private const int MaxSupportedZoom = 24;

    private static readonly HashSet<string> SupportedLayerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "background",
        "circle",
        "fill",
        "line",
        "symbol"
    };

    private static readonly HashSet<string> ExpressionOperators = new(StringComparer.Ordinal)
    {
        "!",
        "!=",
        "<",
        "<=",
        "==",
        ">",
        ">=",
        "+",
        "-",
        "*",
        "/",
        "all",
        "any",
        "case",
        "coalesce",
        "concat",
        "get",
        "has",
        "interpolate",
        "literal",
        "match",
        "step",
        "to-number",
        "to-string",
        "typeof"
    };

    public static bool TryNormalize(
        JsonElement style,
        StyleLayerDescriptor layer,
        out string normalizedJson,
        out string? error)
    {
        normalizedJson = string.Empty;
        error = null;

        if (style.ValueKind != JsonValueKind.Object)
        {
            error = "MapLibre style must be a JSON object.";
            return false;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(style.GetRawText());
        }
        catch (JsonException)
        {
            error = "MapLibre style is not valid JSON.";
            return false;
        }

        if (rootNode is not JsonObject root)
        {
            error = "MapLibre style must be a JSON object.";
            return false;
        }

        if (!TryGetVersion(root, out var version))
        {
            error = "MapLibre style must include a version number.";
            return false;
        }

        if (version != 8)
        {
            error = "MapLibre style version must be 8.";
            return false;
        }

        if (root["layers"] is not JsonArray layers || layers.Count == 0)
        {
            error = "MapLibre style must include at least one layer.";
            return false;
        }

        if (root["sources"] is not null && root["sources"] is not JsonObject)
        {
            error = "MapLibre style sources must be a JSON object.";
            return false;
        }

        var sources = root["sources"] as JsonObject ?? new JsonObject();
        root["sources"] = sources;

        var sourceId = StyleDefaults.GetSourceId(layer);
        sources[sourceId] = BuildSourceNode(layer.Id);

        var hasLayerSource = false;
        var seenLayerIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < layers.Count; index++)
        {
            var layerNode = layers[index];
            if (layerNode is not JsonObject layerObject)
            {
                error = $"MapLibre style layer at index {index} must be a JSON object.";
                return false;
            }

            if (!TryValidateLayer(
                    layerObject,
                    index,
                    sources,
                    sourceId,
                    seenLayerIds,
                    out var usesHonuaSource,
                    out error))
            {
                return false;
            }

            hasLayerSource |= usesHonuaSource;
        }

        if (!hasLayerSource)
        {
            error = "MapLibre style must include a layer using the Honua tile source.";
            return false;
        }

        normalizedJson = root.ToJsonString();
        return true;
    }

    private static bool TryValidateLayer(
        JsonObject layerObject,
        int index,
        JsonObject sources,
        string honuaSourceId,
        HashSet<string> seenLayerIds,
        out bool usesHonuaSource,
        out string? error)
    {
        usesHonuaSource = false;
        error = null;

        var layerId = TryGetString(layerObject["id"]);
        if (string.IsNullOrWhiteSpace(layerId))
        {
            error = $"MapLibre style layer at index {index} must include a non-empty id.";
            return false;
        }

        if (!seenLayerIds.Add(layerId))
        {
            error = $"MapLibre style layer id '{layerId}' is duplicated.";
            return false;
        }

        var layerType = TryGetString(layerObject["type"]);
        if (string.IsNullOrWhiteSpace(layerType))
        {
            error = $"MapLibre style layer '{layerId}' must include a non-empty type.";
            return false;
        }

        if (!SupportedLayerTypes.Contains(layerType))
        {
            error = $"MapLibre style layer '{layerId}' uses unsupported type '{layerType}'.";
            return false;
        }

        if (!TryValidateZoomRange(layerObject, layerId, out error))
        {
            return false;
        }

        if (!TryValidatePaint(layerObject["paint"], layerId, layerType, out error)
            || !TryValidateLayout(layerObject["layout"], layerId, layerType, out error))
        {
            return false;
        }

        if (layerObject["filter"] is { } filter
            && (filter is not JsonArray
                || !TryValidateExpression(filter, $"filter for MapLibre style layer '{layerId}'", out error)))
        {
            error ??= $"MapLibre style layer '{layerId}' filter must be an expression array.";
            return false;
        }

        if (string.Equals(layerType, "background", StringComparison.OrdinalIgnoreCase))
        {
            if (layerObject["source"] is not null || layerObject["source-layer"] is not null)
            {
                error = $"MapLibre background layer '{layerId}' must not declare a source.";
                return false;
            }

            return true;
        }

        if (layerObject["source"] is null)
        {
            layerObject["source"] = honuaSourceId;
        }

        var sourceValue = TryGetString(layerObject["source"]);
        if (string.IsNullOrWhiteSpace(sourceValue))
        {
            error = $"MapLibre style layer '{layerId}' must include a non-empty source.";
            return false;
        }

        if (sources[sourceValue] is not JsonObject sourceObject)
        {
            error = $"MapLibre style layer '{layerId}' references undefined source '{sourceValue}'.";
            return false;
        }

        var sourceType = TryGetString(sourceObject["type"]);
        if (string.IsNullOrWhiteSpace(sourceType))
        {
            error = $"MapLibre style source '{sourceValue}' must include a non-empty type.";
            return false;
        }

        if (string.Equals(sourceType, "vector", StringComparison.OrdinalIgnoreCase))
        {
            if (layerObject["source-layer"] is null)
            {
                if (string.Equals(sourceValue, honuaSourceId, StringComparison.OrdinalIgnoreCase))
                {
                    layerObject["source-layer"] = StyleDefaults.SourceLayerName;
                }
                else
                {
                    error = $"MapLibre style layer '{layerId}' must include source-layer for vector source '{sourceValue}'.";
                    return false;
                }
            }

            var sourceLayer = TryGetString(layerObject["source-layer"]);
            if (string.IsNullOrWhiteSpace(sourceLayer))
            {
                error = $"MapLibre style layer '{layerId}' must include a non-empty source-layer for vector source '{sourceValue}'.";
                return false;
            }

            if (string.Equals(sourceValue, honuaSourceId, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(sourceLayer, StyleDefaults.SourceLayerName, StringComparison.Ordinal))
            {
                error = $"MapLibre style layer '{layerId}' must use source-layer '{StyleDefaults.SourceLayerName}' for the Honua vector source.";
                return false;
            }
        }
        else if (string.Equals(sourceType, "geojson", StringComparison.OrdinalIgnoreCase))
        {
            if (layerObject["source-layer"] is not null)
            {
                error = $"MapLibre style layer '{layerId}' must not include source-layer for GeoJSON source '{sourceValue}'.";
                return false;
            }
        }
        else
        {
            error = $"MapLibre style layer '{layerId}' type '{layerType}' requires a vector or GeoJSON source, but source '{sourceValue}' is '{sourceType}'.";
            return false;
        }

        usesHonuaSource = string.Equals(sourceValue, honuaSourceId, StringComparison.OrdinalIgnoreCase);
        return true;
    }

    private static bool TryValidateZoomRange(JsonObject layerObject, string layerId, out string? error)
    {
        error = null;

        var minZoom = TryReadZoom(layerObject["minzoom"], out var parsedMinZoom) ? parsedMinZoom : (double?)null;
        var maxZoom = TryReadZoom(layerObject["maxzoom"], out var parsedMaxZoom) ? parsedMaxZoom : (double?)null;

        if (layerObject["minzoom"] is not null && minZoom is null)
        {
            error = $"MapLibre style layer '{layerId}' minzoom must be a finite number.";
            return false;
        }

        if (layerObject["maxzoom"] is not null && maxZoom is null)
        {
            error = $"MapLibre style layer '{layerId}' maxzoom must be a finite number.";
            return false;
        }

        if (minZoom is < MinSupportedZoom or > MaxSupportedZoom)
        {
            error = $"MapLibre style layer '{layerId}' minzoom must be between {MinSupportedZoom} and {MaxSupportedZoom}.";
            return false;
        }

        if (maxZoom is < MinSupportedZoom or > MaxSupportedZoom)
        {
            error = $"MapLibre style layer '{layerId}' maxzoom must be between {MinSupportedZoom} and {MaxSupportedZoom}.";
            return false;
        }

        if (minZoom.HasValue && maxZoom.HasValue && minZoom.Value >= maxZoom.Value)
        {
            error = $"MapLibre style layer '{layerId}' minzoom must be less than maxzoom.";
            return false;
        }

        return true;
    }

    private static bool TryReadZoom(JsonNode? node, out double value)
    {
        value = 0;
        if (node is null)
        {
            return false;
        }

        return TryGetFiniteDouble(node, out value);
    }

    private static bool TryValidatePaint(JsonNode? paintNode, string layerId, string layerType, out string? error)
    {
        error = null;
        if (paintNode is null)
        {
            return true;
        }

        if (paintNode is not JsonObject paint)
        {
            error = $"MapLibre style layer '{layerId}' paint must be a JSON object.";
            return false;
        }

        foreach (var property in paint)
        {
            if (!IsSupportedPaintProperty(layerType, property.Key))
            {
                error = $"MapLibre style layer '{layerId}' uses unsupported paint property '{property.Key}'.";
                return false;
            }

            if (!TryValidateStyleProperty(property.Key, property.Value, layerId, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateLayout(JsonNode? layoutNode, string layerId, string layerType, out string? error)
    {
        error = null;
        if (layoutNode is null)
        {
            return true;
        }

        if (layoutNode is not JsonObject layout)
        {
            error = $"MapLibre style layer '{layerId}' layout must be a JSON object.";
            return false;
        }

        foreach (var property in layout)
        {
            if (!IsSupportedLayoutProperty(layerType, property.Key))
            {
                error = $"MapLibre style layer '{layerId}' uses unsupported layout property '{property.Key}'.";
                return false;
            }

            if (!TryValidateStyleProperty(property.Key, property.Value, layerId, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSupportedPaintProperty(string layerType, string property)
        => layerType.ToLowerInvariant() switch
        {
            "circle" => property is "circle-color" or "circle-opacity" or "circle-radius" or "circle-stroke-color" or "circle-stroke-opacity" or "circle-stroke-width",
            "fill" => property is "fill-antialias" or "fill-color" or "fill-opacity" or "fill-outline-color",
            "line" => property is "line-color" or "line-dasharray" or "line-opacity" or "line-width",
            "symbol" => property is "icon-color" or "icon-opacity"
                or "text-color" or "text-opacity"
                or "text-halo-color" or "text-halo-width" or "text-halo-blur",
            _ => false
        };

    private static bool IsSupportedLayoutProperty(string layerType, string property)
        => property == "visibility"
            || (string.Equals(layerType, "line", StringComparison.OrdinalIgnoreCase)
                && property is "line-cap" or "line-join")
            || (string.Equals(layerType, "symbol", StringComparison.OrdinalIgnoreCase)
                && property is "icon-image" or "icon-offset" or "icon-rotate" or "icon-size"
                    or "text-field" or "text-font" or "text-size");

    private static bool TryValidateStyleProperty(string property, JsonNode? value, string layerId, out string? error)
    {
        error = null;
        return property switch
        {
            "circle-color" or "circle-stroke-color" or "fill-color" or "fill-outline-color" or "line-color"
                or "icon-color" or "text-color" or "text-halo-color" =>
                TryValidateColorValue(value, property, layerId, out error),

            "circle-opacity" or "circle-stroke-opacity" or "fill-opacity" or "line-opacity"
                or "icon-opacity" or "text-opacity" =>
                TryValidateOpacityValue(value, property, layerId, out error),

            "circle-radius" or "circle-stroke-width" or "icon-size" or "line-width"
                or "text-size" or "text-halo-width" or "text-halo-blur" =>
                TryValidateNumberValue(value, property, layerId, allowNegative: false, out error),

            "icon-rotate" =>
                TryValidateNumberValue(value, property, layerId, allowNegative: true, out error),

            "fill-antialias" =>
                TryValidateBooleanValue(value, property, layerId, out error),

            "icon-image" or "text-field" =>
                TryValidateStringValue(value, property, layerId, out error),

            "text-font" =>
                TryValidateStringArray(value, property, layerId, out error),

            "line-dasharray" =>
                TryValidateNumberArray(value, property, layerId, requiredLength: null, allowNegative: false, out error),

            "icon-offset" =>
                TryValidateNumberArray(value, property, layerId, requiredLength: 2, allowNegative: true, out error),

            "visibility" =>
                TryValidateEnumValue(value, property, layerId, ["none", "visible"], out error),

            "line-cap" =>
                TryValidateEnumValue(value, property, layerId, ["butt", "round", "square"], out error),

            "line-join" =>
                TryValidateEnumValue(value, property, layerId, ["bevel", "miter", "round"], out error),

            _ => true
        };
    }

    private static bool TryValidateColorValue(JsonNode? value, string property, string layerId, out string? error)
    {
        error = null;
        if (TryGetString(value) is { Length: > 0 } color)
        {
            if (IsValidColorLiteral(color))
            {
                return true;
            }

            error = $"MapLibre style layer '{layerId}' property '{property}' must be a valid CSS color string or expression.";
            return false;
        }

        if (IsExpression(value))
        {
            return TryValidateExpression(value!, $"paint property '{property}' for MapLibre style layer '{layerId}'", out error);
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be a color string or expression.";
        return false;
    }

    private static bool IsValidColorLiteral(string color)
    {
        var trimmed = color.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (CssNamedColors.Contains(trimmed))
        {
            return true;
        }

        if (TryValidateHexColor(trimmed))
        {
            return true;
        }

        return TryValidateColorFunction(trimmed);
    }

    private static bool TryValidateHexColor(string color)
    {
        if (color[0] != '#')
        {
            return false;
        }

        var length = color.Length - 1;
        if (length is not (3 or 4 or 6 or 8))
        {
            return false;
        }

        for (var index = 1; index < color.Length; index++)
        {
            if (!Uri.IsHexDigit(color[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateColorFunction(string color)
    {
        var openParen = color.IndexOf('(');
        if (openParen <= 0 || color[^1] != ')')
        {
            return false;
        }

        var functionName = color[..openParen].Trim();
        var body = color[(openParen + 1)..^1].Trim();
        if (body.Length == 0)
        {
            return false;
        }

        return functionName.ToLowerInvariant() switch
        {
            "rgb" => TryValidateRgbFunction(body, allowAlpha: true),
            "rgba" => TryValidateRgbFunction(body, allowAlpha: true),
            "hsl" => TryValidateHslFunction(body, allowAlpha: true),
            "hsla" => TryValidateHslFunction(body, allowAlpha: true),
            _ => false
        };
    }

    private static bool TryValidateRgbFunction(string body, bool allowAlpha)
    {
        var components = SplitCssFunctionArguments(body);
        if (components.Length != 3 && !(allowAlpha && components.Length == 4))
        {
            return false;
        }

        return TryValidateRgbComponent(components[0]) &&
               TryValidateRgbComponent(components[1]) &&
               TryValidateRgbComponent(components[2]) &&
               (components.Length == 3 || TryValidateAlphaComponent(components[3]));
    }

    private static bool TryValidateHslFunction(string body, bool allowAlpha)
    {
        var components = SplitCssFunctionArguments(body);
        if (components.Length != 3 && !(allowAlpha && components.Length == 4))
        {
            return false;
        }

        return TryValidateFiniteCssNumber(StripHueUnit(components[0]), out _) &&
               TryValidatePercentageComponent(components[1]) &&
               TryValidatePercentageComponent(components[2]) &&
               (components.Length == 3 || TryValidateAlphaComponent(components[3]));
    }

    private static string StripHueUnit(string component)
    {
        var trimmed = component.Trim();
        foreach (var unit in new[] { "deg", "grad", "rad", "turn" })
        {
            if (trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[..^unit.Length];
            }
        }

        return trimmed;
    }

    private static string[] SplitCssFunctionArguments(string body)
    {
        if (body.Contains(','))
        {
            return body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }

        return body.Replace("/", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool TryValidateRgbComponent(string component)
    {
        if (component.EndsWith('%'))
        {
            return TryValidatePercentageComponent(component);
        }

        return TryValidateFiniteCssNumber(component, out var value) && value is >= 0d and <= 255d;
    }

    private static bool TryValidatePercentageComponent(string component)
    {
        if (!component.EndsWith('%'))
        {
            return false;
        }

        return TryValidateFiniteCssNumber(component[..^1], out var value) && value is >= 0d and <= 100d;
    }

    private static bool TryValidateAlphaComponent(string component)
    {
        if (component.EndsWith('%'))
        {
            return TryValidatePercentageComponent(component);
        }

        return TryValidateFiniteCssNumber(component, out var value) && value is >= 0d and <= 1d;
    }

    private static bool TryValidateFiniteCssNumber(string component, out double value)
    {
        return double.TryParse(component, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               double.IsFinite(value);
    }

    private static bool TryValidateOpacityValue(JsonNode? value, string property, string layerId, out string? error)
    {
        error = null;
        if (TryGetFiniteDouble(value, out var opacity))
        {
            if (opacity is < 0d or > 1d)
            {
                error = $"MapLibre style layer '{layerId}' property '{property}' must be between 0 and 1.";
                return false;
            }

            return true;
        }

        if (IsExpression(value))
        {
            return TryValidateExpression(value!, $"paint property '{property}' for MapLibre style layer '{layerId}'", out error);
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be a number or expression.";
        return false;
    }

    private static bool TryValidateNumberValue(
        JsonNode? value,
        string property,
        string layerId,
        bool allowNegative,
        out string? error)
    {
        error = null;
        if (TryGetFiniteDouble(value, out var number))
        {
            if (!allowNegative && number < 0d)
            {
                error = $"MapLibre style layer '{layerId}' property '{property}' must be non-negative.";
                return false;
            }

            return true;
        }

        if (IsExpression(value))
        {
            return TryValidateExpression(value!, $"paint/layout property '{property}' for MapLibre style layer '{layerId}'", out error);
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be a number or expression.";
        return false;
    }

    private static bool TryValidateBooleanValue(JsonNode? value, string property, string layerId, out string? error)
    {
        error = null;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<bool>(out _))
        {
            return true;
        }

        if (IsExpression(value))
        {
            return TryValidateExpression(value!, $"paint/layout property '{property}' for MapLibre style layer '{layerId}'", out error);
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be a boolean or expression.";
        return false;
    }

    private static bool TryValidateStringValue(JsonNode? value, string property, string layerId, out string? error)
    {
        error = null;
        if (!string.IsNullOrWhiteSpace(TryGetString(value)))
        {
            return true;
        }

        if (IsExpression(value))
        {
            return TryValidateExpression(value!, $"layout property '{property}' for MapLibre style layer '{layerId}'", out error);
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be a non-empty string or expression.";
        return false;
    }

    private static bool TryValidateEnumValue(
        JsonNode? value,
        string property,
        string layerId,
        string[] allowed,
        out string? error)
    {
        var text = TryGetString(value);
        if (text != null && allowed.Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            error = null;
            return true;
        }

        error = $"MapLibre style layer '{layerId}' property '{property}' must be one of: {string.Join(", ", allowed)}.";
        return false;
    }

    private static bool TryValidateStringArray(
        JsonNode? value,
        string property,
        string layerId,
        out string? error)
    {
        error = null;
        var array = value as JsonArray;
        if (array is { Count: > 0 }
            && array[0] is JsonValue firstValue
            && firstValue.TryGetValue<string>(out var op)
            && string.Equals(op, "literal", StringComparison.Ordinal))
        {
            array = array.Count > 1 ? array[1] as JsonArray : null;
        }

        if (array is null)
        {
            if (IsExpression(value))
            {
                return TryValidateExpression(value!, $"layout property '{property}' for MapLibre style layer '{layerId}'", out error);
            }

            error = $"MapLibre style layer '{layerId}' property '{property}' must be an array of strings.";
            return false;
        }

        if (array.Count == 0)
        {
            error = $"MapLibre style layer '{layerId}' property '{property}' must contain at least one string.";
            return false;
        }

        foreach (var item in array)
        {
            if (string.IsNullOrWhiteSpace(TryGetString(item)))
            {
                error = $"MapLibre style layer '{layerId}' property '{property}' must contain only non-empty strings.";
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateNumberArray(
        JsonNode? value,
        string property,
        string layerId,
        int? requiredLength,
        bool allowNegative,
        out string? error)
    {
        error = null;
        var array = value as JsonArray;
        if (array is { Count: > 0 }
            && array[0] is JsonValue firstValue
            && firstValue.TryGetValue<string>(out var op)
            && string.Equals(op, "literal", StringComparison.Ordinal))
        {
            array = array.Count > 1 ? array[1] as JsonArray : null;
        }

        if (array is null)
        {
            error = $"MapLibre style layer '{layerId}' property '{property}' must be an array of numbers.";
            return false;
        }

        if (requiredLength.HasValue && array.Count != requiredLength.Value)
        {
            error = $"MapLibre style layer '{layerId}' property '{property}' must contain {requiredLength.Value} numbers.";
            return false;
        }

        if (array.Count == 0)
        {
            error = $"MapLibre style layer '{layerId}' property '{property}' must contain at least one number.";
            return false;
        }

        foreach (var item in array)
        {
            if (!TryGetFiniteDouble(item, out var number) || (!allowNegative && number < 0d))
            {
                var qualifier = allowNegative ? "finite" : "non-negative";
                error = $"MapLibre style layer '{layerId}' property '{property}' must contain only {qualifier} numbers.";
                return false;
            }
        }

        return true;
    }

    private static bool IsExpression(JsonNode? value)
        => value is JsonArray { Count: > 0 } array
            && TryGetString(array[0]) is { } op
            && ExpressionOperators.Contains(op);

    private static bool TryValidateExpression(JsonNode expressionNode, string context, out string? error)
    {
        using var document = JsonDocument.Parse(expressionNode.ToJsonString());
        return TryValidateExpression(document.RootElement, context, out error);
    }

    private static bool TryValidateExpression(JsonElement expression, string context, out string? error)
    {
        error = null;
        switch (expression.ValueKind)
        {
            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                return true;
            case JsonValueKind.Array:
                return TryValidateExpressionArray(expression, context, out error);
            default:
                error = $"MapLibre expression in {context} must be a literal or expression array.";
                return false;
        }
    }

    private static bool TryValidateExpressionArray(JsonElement expression, string context, out string? error)
    {
        error = null;
        var items = expression.EnumerateArray().ToArray();
        if (items.Length == 0)
        {
            error = $"MapLibre expression in {context} must not be an empty array.";
            return false;
        }

        if (items[0].ValueKind != JsonValueKind.String)
        {
            error = $"MapLibre expression in {context} must start with an operator string.";
            return false;
        }

        var op = items[0].GetString();
        if (string.IsNullOrWhiteSpace(op) || !ExpressionOperators.Contains(op))
        {
            error = $"MapLibre expression in {context} uses unsupported operator '{op}'.";
            return false;
        }

        return op switch
        {
            "get" or "has" => TryValidateGetOrHasExpression(items, context, op, out error),
            "!" or "to-number" or "to-string" or "typeof" => TryValidateFixedArityExpression(items, context, op, 2, out error),
            "==" or "!=" or "<" or "<=" or ">" or ">=" => TryValidateFixedArityExpression(items, context, op, 3, out error),
            "case" => TryValidateCaseExpression(items, context, out error),
            "match" => TryValidateMatchExpression(items, context, out error),
            "step" => TryValidateStepExpression(items, context, out error),
            "interpolate" => TryValidateInterpolateExpression(items, context, out error),
            "literal" => TryValidateFixedArityExpression(items, context, op, 2, validateChildren: false, out error),
            "all" or "any" or "coalesce" or "concat" => TryValidateVariadicExpression(items, context, op, 2, out error),
            "+" or "-" or "*" or "/" => TryValidateVariadicExpression(items, context, op, 3, out error),
            _ => true
        };
    }

    private static bool TryValidateGetOrHasExpression(JsonElement[] items, string context, string op, out string? error)
    {
        error = null;
        if (items.Length != 2 || items[1].ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(items[1].GetString()))
        {
            error = $"MapLibre expression '{op}' in {context} must have a single string field argument.";
            return false;
        }

        return true;
    }

    private static bool TryValidateFixedArityExpression(
        JsonElement[] items,
        string context,
        string op,
        int expectedLength,
        out string? error)
        => TryValidateFixedArityExpression(items, context, op, expectedLength, validateChildren: true, out error);

    private static bool TryValidateFixedArityExpression(
        JsonElement[] items,
        string context,
        string op,
        int expectedLength,
        bool validateChildren,
        out string? error)
    {
        if (items.Length != expectedLength)
        {
            error = $"MapLibre expression '{op}' in {context} must have {expectedLength - 1} argument(s).";
            return false;
        }

        error = null;
        if (!validateChildren)
        {
            return true;
        }

        for (var i = 1; i < items.Length; i++)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateVariadicExpression(
        JsonElement[] items,
        string context,
        string op,
        int minLength,
        out string? error)
    {
        if (items.Length < minLength)
        {
            error = $"MapLibre expression '{op}' in {context} must have at least {minLength - 1} argument(s).";
            return false;
        }

        error = null;
        for (var i = 1; i < items.Length; i++)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateCaseExpression(JsonElement[] items, string context, out string? error)
    {
        if (items.Length < 4 || items.Length % 2 != 0)
        {
            error = $"MapLibre expression 'case' in {context} must include condition/result pairs and a fallback.";
            return false;
        }

        error = null;
        for (var i = 1; i < items.Length; i++)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateMatchExpression(JsonElement[] items, string context, out string? error)
    {
        if (items.Length < 5 || items.Length % 2 == 0)
        {
            error = $"MapLibre expression 'match' in {context} must include labels, outputs, and a fallback.";
            return false;
        }

        if (!TryValidateExpression(items[1], context, out error))
        {
            return false;
        }

        for (var i = 3; i < items.Length - 1; i += 2)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return TryValidateExpression(items[^1], context, out error);
    }

    private static bool TryValidateStepExpression(JsonElement[] items, string context, out string? error)
    {
        if (items.Length < 5 || (items.Length - 3) % 2 != 0)
        {
            error = $"MapLibre expression 'step' in {context} must include input, default output, and stop/output pairs.";
            return false;
        }

        error = null;
        for (var i = 1; i < items.Length; i++)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateInterpolateExpression(JsonElement[] items, string context, out string? error)
    {
        if (items.Length < 5 || (items.Length - 3) % 2 != 0)
        {
            error = $"MapLibre expression 'interpolate' in {context} must include interpolation, input, and stop/output pairs.";
            return false;
        }

        if (!TryValidateInterpolationSpec(items[1], context, out error)
            || !TryValidateExpression(items[2], context, out error))
        {
            return false;
        }

        for (var i = 3; i < items.Length; i++)
        {
            if (!TryValidateExpression(items[i], context, out error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateInterpolationSpec(JsonElement spec, string context, out string? error)
    {
        var items = spec.ValueKind == JsonValueKind.Array ? spec.EnumerateArray().ToArray() : [];
        if (items.Length == 0 || items[0].ValueKind != JsonValueKind.String)
        {
            error = $"MapLibre expression 'interpolate' in {context} must include an interpolation spec.";
            return false;
        }

        var interpolation = items[0].GetString();
        if (interpolation == "linear" && items.Length == 1)
        {
            error = null;
            return true;
        }

        if (interpolation == "exponential"
            && items.Length == 2
            && items[1].ValueKind == JsonValueKind.Number
            && items[1].TryGetDouble(out var baseValue)
            && double.IsFinite(baseValue)
            && baseValue > 0d)
        {
            error = null;
            return true;
        }

        error = $"MapLibre expression 'interpolate' in {context} has an invalid interpolation spec.";
        return false;
    }

    private static JsonObject BuildSourceNode(int layerId)
    {
        return new JsonObject
        {
            ["type"] = "vector",
            ["tiles"] = new JsonArray(StyleDefaults.GetTileUrl(layerId)),
            ["minzoom"] = 0,
            ["maxzoom"] = 22
        };
    }

    private static bool TryGetVersion(JsonObject root, out int version)
    {
        version = 0;

        if (root["version"] is not JsonNode versionNode)
        {
            return false;
        }

        if (versionNode is JsonValue valueNode && valueNode.TryGetValue<int>(out var parsed))
        {
            version = parsed;
            return true;
        }

        if (versionNode is JsonValue stringNode && stringNode.TryGetValue<string>(out var text)
            && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedText))
        {
            version = parsedText;
            return true;
        }

        return false;
    }

    private static bool TryGetFiniteDouble(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue jsonValue)
        {
            return false;
        }

        if (jsonValue.TryGetValue<double>(out var parsed)
            && double.IsFinite(parsed))
        {
            value = parsed;
            return true;
        }

        if (jsonValue.TryGetValue<int>(out var intValue))
        {
            value = intValue;
            return true;
        }

        if (jsonValue.TryGetValue<long>(out var longValue))
        {
            value = longValue;
            return true;
        }

        return false;
    }

    private static string? TryGetString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        return null;
    }
}
