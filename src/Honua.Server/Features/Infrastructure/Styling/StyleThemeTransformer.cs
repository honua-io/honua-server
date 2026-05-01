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
                var hsl = RgbToHsl(color);
                var inverted = HslToRgb(hsl with { L = 1d - hsl.L });
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
        var index = 0;
        foreach (var layer in layers)
        {
            if (layer is not JsonObject layerObject)
            {
                continue;
            }

            TransformPaintColors(layerObject, diagnostics, _ =>
            {
                var paletteHex = paletteColors[index % paletteColors.Length];
                index++;
                if (StyleJsonUtilities.TryParseMapLibreColor(paletteHex, out var swap))
                {
                    return swap;
                }
                return new StyleColor(0, 0, 0, 255);
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
                    if (paint[opacityProperty] is JsonValue)
                    {
                        paint[opacityProperty] = 1d;
                    }
                }

                if (string.Equals(layerType, "line", StringComparison.OrdinalIgnoreCase)
                    && paint["line-color"] is JsonValue)
                {
                    paint["line-color"] = PrintLineColor;
                }

                if (string.Equals(layerType, "fill", StringComparison.OrdinalIgnoreCase)
                    && (paint["fill-outline-color"] is null || paint["fill-outline-color"] is not JsonValue))
                {
                    paint["fill-outline-color"] = PrintLineColor;
                }
                else if (string.Equals(layerType, "fill", StringComparison.OrdinalIgnoreCase)
                    && paint["fill-outline-color"] is JsonValue)
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
    /// Walks a MapLibre expression array (e.g. `["case", ..., ["match", ...], "fallback"]`)
    /// and rewrites every embedded string color literal through <paramref name="transform"/>.
    /// Numeric, boolean, and operator string tokens are left untouched: any string that
    /// fails <see cref="StyleJsonUtilities.TryParseMapLibreColor(string?, out StyleColor)"/>
    /// is preserved as-is so operator names like <c>match</c> are not corrupted.
    /// </summary>
    private static void TransformExpressionColors(
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

    private readonly record struct HslColor(double H, double S, double L);

    private static HslColor RgbToHsl(StyleColor color)
    {
        var r = color.R / 255d;
        var g = color.G / 255d;
        var b = color.B / 255d;

        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var lightness = (max + min) / 2d;

        if (Math.Abs(max - min) < double.Epsilon)
        {
            return new HslColor(0d, 0d, lightness);
        }

        var delta = max - min;
        var saturation = lightness > 0.5d
            ? delta / (2d - max - min)
            : delta / (max + min);

        double hue;
        if (max == r)
        {
            hue = ((g - b) / delta) + (g < b ? 6d : 0d);
        }
        else if (max == g)
        {
            hue = ((b - r) / delta) + 2d;
        }
        else
        {
            hue = ((r - g) / delta) + 4d;
        }

        hue *= 60d;
        return new HslColor(hue, saturation, lightness);
    }

    private static StyleColor HslToRgb(HslColor hsl)
    {
        if (hsl.S < double.Epsilon)
        {
            var grey = (byte)Math.Clamp(Math.Round(hsl.L * 255d, MidpointRounding.AwayFromZero), 0d, 255d);
            return new StyleColor(grey, grey, grey, 255);
        }

        var q = hsl.L < 0.5d
            ? hsl.L * (1d + hsl.S)
            : hsl.L + hsl.S - (hsl.L * hsl.S);
        var p = (2d * hsl.L) - q;
        var hueNormalized = hsl.H / 360d;

        var r = HueToChannel(p, q, hueNormalized + (1d / 3d));
        var g = HueToChannel(p, q, hueNormalized);
        var b = HueToChannel(p, q, hueNormalized - (1d / 3d));

        return new StyleColor(
            (byte)Math.Clamp(Math.Round(r * 255d, MidpointRounding.AwayFromZero), 0d, 255d),
            (byte)Math.Clamp(Math.Round(g * 255d, MidpointRounding.AwayFromZero), 0d, 255d),
            (byte)Math.Clamp(Math.Round(b * 255d, MidpointRounding.AwayFromZero), 0d, 255d),
            255);
    }

    private static double HueToChannel(double p, double q, double t)
    {
        if (t < 0d)
        {
            t += 1d;
        }
        if (t > 1d)
        {
            t -= 1d;
        }

        if (t < 1d / 6d)
        {
            return p + ((q - p) * 6d * t);
        }
        if (t < 1d / 2d)
        {
            return q;
        }
        if (t < 2d / 3d)
        {
            return p + ((q - p) * ((2d / 3d) - t) * 6d);
        }
        return p;
    }
}
