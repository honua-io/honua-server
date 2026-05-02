// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Honua.Server.Features.Infrastructure.Styling;

internal static class StyleJsonUtilities
{
    public static string Serialize(Dictionary<string, object?> payload)
        => JsonSerializer.Serialize(payload, StyleJsonContext.Default.DictionaryStringObject);

    public static JsonElement? ParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryParseGeoServicesColor(JsonElement element, out StyleColor color)
    {
        color = default;

        if (element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var channels = element.EnumerateArray().ToArray();
        if (channels.Length < 3)
        {
            return false;
        }

        if (!TryGetByte(channels[0], out var r)
            || !TryGetByte(channels[1], out var g)
            || !TryGetByte(channels[2], out var b))
        {
            return false;
        }

        var a = (byte)255;
        if (channels.Length >= 4 && TryGetByte(channels[3], out var alpha))
        {
            a = alpha;
        }

        color = new StyleColor(r, g, b, a);
        return true;
    }

    public static bool TryParseMapLibreColor(JsonElement element, out StyleColor color)
    {
        color = default;

        if (element.ValueKind == JsonValueKind.String)
        {
            return TryParseMapLibreColor(element.GetString(), out color);
        }

        return false;
    }

    public static bool TryParseMapLibreColor(string? text, out StyleColor color)
    {
        color = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (trimmed.StartsWith('#'))
        {
            return TryParseHexColor(trimmed, out color);
        }

        // The admin write-time normalizer (MapLibreStyleNormalizer.IsValidColorLiteral)
        // accepts rgb / rgba / hsl / hsla in both legacy comma-separated and modern
        // CSS Color Module Level 4 space/slash-separated forms.  Theme transforms
        // must accept the same set so a stored "hsl(120 100% 50%)" or
        // "rgb(255 0 0 / 0.5)" is themed instead of being treated as malformed.
        if (trimmed.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbColor(trimmed, out color);
        }

        if (trimmed.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHslColor(trimmed, out color);
        }

        // Named CSS / X11 colors are accepted by the admin write-time normalizer
        // (MapLibreStyleNormalizer.IsValidColorLiteral) so the theme transformer
        // must resolve them too — otherwise a stored "red" would be treated as
        // malformed and skipped under ?theme=dark|colorblind-safe|print.
        return CssNamedColors.TryGet(trimmed, out color);
    }

    public static double ClampOpacity(double value)
        => Math.Max(0d, Math.Min(1d, value));

    private static bool TryParseHexColor(string text, out StyleColor color)
    {
        color = default;

        var hex = text.TrimStart('#');
        if (hex.Length is 3 or 4)
        {
            hex = string.Concat(hex.Select(c => string.Concat(c, c)));
        }

        if (hex.Length is not (6 or 8))
        {
            return false;
        }

        if (!TryParseHexByte(hex.AsSpan(0, 2), out var r)
            || !TryParseHexByte(hex.AsSpan(2, 2), out var g)
            || !TryParseHexByte(hex.AsSpan(4, 2), out var b))
        {
            return false;
        }

        var a = (byte)255;
        if (hex.Length == 8 && !TryParseHexByte(hex.AsSpan(6, 2), out a))
        {
            return false;
        }

        color = new StyleColor(r, g, b, a);
        return true;
    }

    private static bool TryParseHexByte(ReadOnlySpan<char> span, out byte value)
    {
        value = 0;
        if (!int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        value = (byte)Math.Clamp(parsed, 0, 255);
        return true;
    }

    private static bool TryParseRgbColor(string text, out StyleColor color)
    {
        color = default;

        if (!TryExtractFunctionArguments(text, out var parts))
        {
            return false;
        }

        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (!TryParseColorChannel(parts[0], out var r)
            || !TryParseColorChannel(parts[1], out var g)
            || !TryParseColorChannel(parts[2], out var b))
        {
            return false;
        }

        var a = (byte)255;
        if (parts.Length == 4)
        {
            if (!TryParseAlphaChannel(parts[3], out var alpha))
            {
                return false;
            }

            a = alpha;
        }

        color = new StyleColor(r, g, b, a);
        return true;
    }

    private static bool TryParseHslColor(string text, out StyleColor color)
    {
        color = default;

        if (!TryExtractFunctionArguments(text, out var parts))
        {
            return false;
        }

        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (!TryParseHueChannel(parts[0], out var hueDegrees)
            || !TryParsePercentageChannel(parts[1], out var saturation)
            || !TryParsePercentageChannel(parts[2], out var lightness))
        {
            return false;
        }

        var alpha = (byte)255;
        if (parts.Length == 4 && !TryParseAlphaChannel(parts[3], out alpha))
        {
            return false;
        }

        var rgb = HslToRgb(new HslColor(NormalizeHueDegrees(hueDegrees), saturation, lightness));
        color = new StyleColor(rgb.R, rgb.G, rgb.B, alpha);
        return true;
    }

    /// <summary>
    /// Splits a CSS color function body on either commas (legacy syntax) or
    /// whitespace + an optional `/` alpha separator (CSS Color Module Level 4
    /// modern syntax).  Mirrors the admin-write normalizer's
    /// <c>SplitCssFunctionArguments</c> helper so the parser accepts every
    /// form the normalizer admits.
    /// </summary>
    private static bool TryExtractFunctionArguments(string text, out string[] parts)
    {
        parts = Array.Empty<string>();

        var start = text.IndexOf('(');
        var end = text.LastIndexOf(')');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var body = text.AsSpan(start + 1, end - start - 1).Trim().ToString();
        if (body.Length == 0)
        {
            return false;
        }

        if (body.Contains(','))
        {
            parts = body.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 0;
        }

        // Modern syntax: components separated by whitespace, optional alpha after `/`.
        var normalized = body.Replace('/', ' ');
        parts = normalized.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0;
    }

    private static bool TryParseHueChannel(string input, out double degrees)
    {
        degrees = 0d;

        var trimmed = input.Trim();
        var unit = "deg";
        foreach (var candidate in new[] { "deg", "grad", "rad", "turn" })
        {
            if (trimmed.EndsWith(candidate, StringComparison.OrdinalIgnoreCase))
            {
                unit = candidate;
                trimmed = trimmed[..^candidate.Length].TrimEnd();
                break;
            }
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            return false;
        }

        degrees = unit switch
        {
            "grad" => parsed * 0.9d,
            "rad" => parsed * (180d / Math.PI),
            "turn" => parsed * 360d,
            _ => parsed
        };
        return true;
    }

    private static bool TryParsePercentageChannel(string input, out double fraction)
    {
        fraction = 0d;

        var trimmed = input.Trim();
        if (!trimmed.EndsWith('%'))
        {
            return false;
        }

        if (!double.TryParse(
                trimmed.AsSpan(0, trimmed.Length - 1),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return false;
        }

        fraction = Math.Clamp(parsed / 100d, 0d, 1d);
        return true;
    }

    private static double NormalizeHueDegrees(double degrees)
    {
        var modulo = degrees % 360d;
        if (modulo < 0d)
        {
            modulo += 360d;
        }
        return modulo;
    }

    internal readonly record struct HslColor(double H, double S, double L);

    internal static StyleColor HslToRgb(HslColor hsl)
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

    internal static HslColor RgbToHsl(StyleColor color)
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

    private static bool TryParseColorChannel(string input, out byte value)
    {
        value = 0;

        var trimmed = input.Trim();
        var isPercent = trimmed.EndsWith('%');
        if (isPercent)
        {
            trimmed = trimmed.TrimEnd('%');
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (isPercent)
        {
            parsed = (parsed / 100d) * 255d;
        }
        else if (parsed <= 1d && trimmed.Contains('.', StringComparison.Ordinal))
        {
            parsed *= 255d;
        }

        value = (byte)Math.Clamp(parsed, 0d, 255d);
        return true;
    }

    private static bool TryParseAlphaChannel(string input, out byte value)
    {
        value = 0;

        var trimmed = input.Trim();
        var isPercent = trimmed.EndsWith('%');
        if (isPercent)
        {
            trimmed = trimmed.TrimEnd('%');
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return false;
        }

        if (isPercent)
        {
            parsed = (parsed / 100d) * 255d;
        }
        else if (parsed <= 1d)
        {
            parsed *= 255d;
        }

        value = (byte)Math.Clamp(parsed, 0d, 255d);
        return true;
    }

    private static bool TryGetByte(JsonElement element, out byte value)
    {
        value = 0;

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
        {
            value = (byte)Math.Clamp(intValue, 0, 255);
            return true;
        }

        if (element.ValueKind == JsonValueKind.String
            && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = (byte)Math.Clamp(parsed, 0, 255);
            return true;
        }

        return false;
    }
}

internal readonly record struct StyleColor(byte R, byte G, byte B, byte A)
{
    public double Alpha => A / 255d;

    public int[] ToArray() => [R, G, B, A];

    public StyleColor ApplyOpacity(double opacity)
    {
        var clamped = StyleJsonUtilities.ClampOpacity(opacity);
        var adjusted = (byte)Math.Clamp(Math.Round(A * clamped, MidpointRounding.AwayFromZero), 0, 255);
        return new StyleColor(R, G, B, adjusted);
    }

    public string ToRgbaString()
        => string.Create(CultureInfo.InvariantCulture, $"rgba({R},{G},{B},{Alpha:0.###})");
}
