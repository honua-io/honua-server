// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Core.Features.Styling.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Styling;

/// <summary>
/// Pure deterministic transforms applied to a canonical MapLibre Style Spec v8
/// document after generation.  No I/O; transforms operate on already-serialized
/// JSON and return new JSON.  Same input + same theme always produces the same
/// output, so the result is safe to cache by query key.
/// </summary>
internal static class StyleThemeTransformer
{
    private const string DarkBackgroundColor = "#1a1a1a";
    private const string PrintLineColor = "#000000";

    private static readonly string[] ColorPaintProperties =
    [
        "circle-color",
        "circle-stroke-color",
        "fill-color",
        "fill-outline-color",
        "line-color",
        "icon-color",
        "text-color",
        "text-halo-color",
        "background-color"
    ];

    private static readonly string[] OpacityPaintProperties =
    [
        "circle-opacity",
        "circle-stroke-opacity",
        "fill-opacity",
        "line-opacity",
        "icon-opacity",
        "text-opacity",
        "background-opacity"
    ];

    /// <summary>
    /// Applies the given theme to a canonical MapLibre style document.  Returns
    /// the input unchanged for <see cref="ThemeProfile.Default"/> or when the
    /// payload cannot be parsed.  When a logger is supplied, malformed color
    /// literals encountered during the transform emit event 6403 with the
    /// originating layer id.
    /// </summary>
    public static string ApplyTheme(
        string mapLibreStyleJson,
        ThemeProfile theme,
        ILogger? logger = null,
        int layerId = 0)
    {
        if (theme == ThemeProfile.Default || string.IsNullOrWhiteSpace(mapLibreStyleJson))
        {
            return mapLibreStyleJson;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(mapLibreStyleJson);
        }
        catch (JsonException)
        {
            return mapLibreStyleJson;
        }

        if (root is not JsonObject obj || obj["layers"] is not JsonArray layers)
        {
            return mapLibreStyleJson;
        }

        var diagnostics = new ThemeDiagnostics(logger, layerId);

        switch (theme)
        {
            case ThemeProfile.Dark:
                ApplyDarkTheme(layers, diagnostics);
                break;
            case ThemeProfile.ColorblindSafe:
                ApplyColorblindSafeTheme(layers, diagnostics);
                break;
            case ThemeProfile.Print:
                ApplyPrintTheme(layers);
                break;
        }

        return obj.ToJsonString();
    }

    private static void ApplyDarkTheme(JsonArray layers, ThemeDiagnostics diagnostics)
    {
        foreach (var layer in layers)
        {
            if (layer is not JsonObject layerObject)
            {
                continue;
            }

            var isBackground = string.Equals(GetString(layerObject["type"]), "background", StringComparison.OrdinalIgnoreCase);

            TransformPaintColors(layerObject, diagnostics, color =>
            {
                var hsl = StyleJsonUtilities.RgbToHsl(color);
                var inverted = StyleJsonUtilities.HslToRgb(hsl with { L = 1d - hsl.L });
                return new StyleColor(inverted.R, inverted.G, inverted.B, color.A);
            });

            if (isBackground && layerObject["paint"] is JsonObject paint && paint["background-color"] is not null)
            {
                paint["background-color"] = DarkBackgroundColor;
            }
        }
    }

    private static void ApplyColorblindSafeTheme(JsonArray layers, ThemeDiagnostics diagnostics)
    {
        var palette = ColorPalettes.Viridis.Colors;
        var maxClasses = 0;
        foreach (var key in palette.Keys)
        {
            if (key > maxClasses)
            {
                maxClasses = key;
            }
        }

        var paletteColors = palette[maxClasses];
        // Memoize palette assignment by full input RGBA within one ApplyTheme call so
        // identical input colors map to the same palette slot (e.g. a classBreaks first
        // class output and its case fallback that share a color stay visually equal),
        // and so the output preserves the input alpha rather than forcing every paint
        // property to fully opaque.
        var assignments = new Dictionary<StyleColor, StyleColor>();
        foreach (var layer in layers)
        {
            if (layer is not JsonObject layerObject)
            {
                continue;
            }

            TransformPaintColors(layerObject, diagnostics, color =>
            {
                if (assignments.TryGetValue(color, out var cached))
                {
                    return cached;
                }

                var paletteHex = paletteColors[assignments.Count % paletteColors.Length];
                StyleColor resolved;
                if (StyleJsonUtilities.TryParseMapLibreColor(paletteHex, out var swap))
                {
                    resolved = new StyleColor(swap.R, swap.G, swap.B, color.A);
                }
                else
                {
                    resolved = new StyleColor(0, 0, 0, color.A);
                }

                assignments[color] = resolved;
                return resolved;
            });
        }
    }

    private static void ApplyPrintTheme(JsonArray layers)
    {
        foreach (var layer in layers)
        {
            if (layer is not JsonObject layerObject)
            {
                continue;
            }

            var layerType = GetString(layerObject["type"])?.ToLowerInvariant();

            if (layerObject["paint"] is JsonObject paint)
            {
                foreach (var opacityProperty in OpacityPaintProperties)
                {
                    // Force every present opacity property to 1.0 regardless of
                    // whether the stored value is a scalar or an expression: the
                    // print contract is "fully opaque", and the normalizer accepts
                    // expression-typed opacity values that would otherwise survive
                    // unchanged here.
                    if (paint.ContainsKey(opacityProperty))
                    {
                        paint[opacityProperty] = 1d;
                    }
                }

                // Force every present line-color on line layers to PrintLineColor
                // regardless of scalar vs expression value: the normalizer accepts
                // expression-typed line-color, and the print contract is "line
                // colors are black". An expression-valued line-color would
                // otherwise survive the transform and stay colored under
                // ?theme=print.
                if (string.Equals(layerType, "line", StringComparison.OrdinalIgnoreCase)
                    && paint["line-color"] is not null)
                {
                    paint["line-color"] = PrintLineColor;
                }

                // Fill layers always get a black outline in print so polygon
                // boundaries are visible against high-contrast print rendering,
                // regardless of whether the stored value is missing, a scalar,
                // or an expression.
                if (string.Equals(layerType, "fill", StringComparison.OrdinalIgnoreCase))
                {
                    paint["fill-outline-color"] = PrintLineColor;
                }
            }
        }
    }

    private static void TransformPaintColors(
        JsonObject layerObject,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        if (layerObject["paint"] is not JsonObject paint)
        {
            return;
        }

        foreach (var property in ColorPaintProperties)
        {
            var value = paint[property];
            if (value is JsonValue valueNode)
            {
                if (!valueNode.TryGetValue<string>(out var colorString) || string.IsNullOrWhiteSpace(colorString))
                {
                    continue;
                }

                if (!StyleJsonUtilities.TryParseMapLibreColor(colorString, out var parsed))
                {
                    diagnostics.RecordMalformedColor(property, colorString);
                    continue;
                }

                var transformed = transform(parsed);
                paint[property] = ColorToHex(transformed);
            }
            else if (value is JsonArray expressionArray)
            {
                TransformExpressionColors(expressionArray, property, diagnostics, transform);
            }
        }
    }

    /// <summary>
    /// Walks a MapLibre expression array and rewrites embedded color literals at
    /// output positions through <paramref name="transform"/>.  The walker is
    /// operator-aware for <c>match</c>, <c>step</c>, and <c>case</c>: feature
    /// match labels, numeric step stops, and case predicates are skipped so
    /// color-like input values (e.g. a <c>uniqueValue</c> category equal to
    /// <c>"#ff0000"</c>) are not silently rewritten.  Unknown operators fall
    /// back to the generic walker so expressions such as <c>interpolate</c> still
    /// pick up direct color literals.
    /// </summary>
    private static void TransformExpressionColors(
        JsonArray expression,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        if (expression.Count == 0)
        {
            return;
        }

        var op = expression[0] is JsonValue operatorNode
            && operatorNode.TryGetValue<string>(out var opName)
            ? opName
            : null;

        switch (op)
        {
            case "match":
                TransformMatchExpression(expression, property, diagnostics, transform);
                return;
            case "step":
                TransformStepExpression(expression, property, diagnostics, transform);
                return;
            case "case":
                TransformCaseExpression(expression, property, diagnostics, transform);
                return;
            default:
                TransformGenericExpression(expression, property, diagnostics, transform);
                return;
        }
    }

    /// <summary>
    /// `["match", input, label1, output1, ..., labelN, outputN, fallback?]`.  Skips
    /// the input expression and every label entirely; transforms each output and
    /// the trailing fallback.
    /// </summary>
    private static void TransformMatchExpression(
        JsonArray expression,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        if (expression.Count < 4)
        {
            return;
        }

        var remaining = expression.Count - 2;
        var hasFallback = remaining % 2 == 1;
        var pairsEnd = hasFallback ? expression.Count - 1 : expression.Count;

        for (var i = 2; i + 1 < pairsEnd; i += 2)
        {
            TransformOutputElement(expression, i + 1, property, diagnostics, transform);
        }

        if (hasFallback)
        {
            TransformOutputElement(expression, expression.Count - 1, property, diagnostics, transform);
        }
    }

    /// <summary>
    /// `["step", input, output0, stop1, output1, stop2, output2, ...]`.  Skips
    /// the input expression and every numeric stop; transforms each output.
    /// </summary>
    private static void TransformStepExpression(
        JsonArray expression,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        if (expression.Count < 3)
        {
            return;
        }

        TransformOutputElement(expression, 2, property, diagnostics, transform);

        for (var i = 4; i < expression.Count; i += 2)
        {
            TransformOutputElement(expression, i, property, diagnostics, transform);
        }
    }

    /// <summary>
    /// `["case", cond1, output1, ..., fallback]`.  Skips every predicate entirely
    /// (predicates may compare against color-like feature values); transforms
    /// each branch output and the mandatory trailing fallback.
    /// </summary>
    private static void TransformCaseExpression(
        JsonArray expression,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        if (expression.Count < 4)
        {
            return;
        }

        var fallbackIndex = expression.Count - 1;

        for (var i = 1; i + 1 < fallbackIndex; i += 2)
        {
            TransformOutputElement(expression, i + 1, property, diagnostics, transform);
        }

        TransformOutputElement(expression, fallbackIndex, property, diagnostics, transform);
    }

    /// <summary>
    /// Recurses into a single expression element treated as a color-bearing
    /// output: nested arrays are walked operator-aware, scalar string values
    /// that parse as colors are rewritten in place.
    /// </summary>
    private static void TransformOutputElement(
        JsonArray expression,
        int index,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        var node = expression[index];
        if (node is JsonArray nested)
        {
            TransformExpressionColors(nested, property, diagnostics, transform);
            return;
        }

        if (node is not JsonValue valueNode)
        {
            return;
        }

        if (!valueNode.TryGetValue<string>(out var colorString) || string.IsNullOrWhiteSpace(colorString))
        {
            return;
        }

        if (!StyleJsonUtilities.TryParseMapLibreColor(colorString, out var parsed))
        {
            diagnostics.RecordMalformedColor(property, colorString);
            return;
        }

        var transformed = transform(parsed);
        expression[index] = ColorToHex(transformed);
    }

    /// <summary>
    /// Generic walker for unknown operators (`interpolate`, `rgb`, `literal`,
    /// boolean comparators, …): recurses into every nested array and rewrites
    /// any direct string child that parses as a color.  Operator tokens and
    /// non-color strings (field names, numeric literals as strings, etc.) are
    /// left untouched because they fail color parsing.
    /// </summary>
    private static void TransformGenericExpression(
        JsonArray expression,
        string property,
        ThemeDiagnostics diagnostics,
        Func<StyleColor, StyleColor> transform)
    {
        for (var i = 0; i < expression.Count; i++)
        {
            var node = expression[i];
            if (node is JsonArray nested)
            {
                TransformExpressionColors(nested, property, diagnostics, transform);
                continue;
            }

            if (node is not JsonValue valueNode)
            {
                continue;
            }

            if (!valueNode.TryGetValue<string>(out var colorString) || string.IsNullOrWhiteSpace(colorString))
            {
                continue;
            }

            if (!StyleJsonUtilities.TryParseMapLibreColor(colorString, out var parsed))
            {
                continue;
            }

            var transformed = transform(parsed);
            expression[i] = ColorToHex(transformed);
        }
    }

    /// <summary>
    /// Carries the optional logger plus contextual layer id used to emit
    /// <see cref="LayerStyleLog.ThemeColorParseFailure"/> (event 6403) without
    /// threading individual ILogger references through every transform helper.
    /// </summary>
    private readonly record struct ThemeDiagnostics(ILogger? Logger, int LayerId)
    {
        public void RecordMalformedColor(string property, string color)
        {
            if (Logger == null)
            {
                return;
            }

            LayerStyleLog.ThemeColorParseFailure(Logger, LayerId, property, color);
        }
    }

    private static string ColorToHex(StyleColor color)
    {
        if (color.A == 255)
        {
            return string.Create(CultureInfo.InvariantCulture, $"#{color.R:x2}{color.G:x2}{color.B:x2}");
        }

        return color.ToRgbaString();
    }

    private static string? GetString(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }
        return null;
    }

}
