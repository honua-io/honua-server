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

        if (trimmed.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbColor(trimmed, out color);
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

        var start = text.IndexOf('(');
        var end = text.IndexOf(')');
        if (start < 0 || end <= start)
        {
            return false;
        }

        var content = text.Substring(start + 1, end - start - 1);
        var parts = content.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
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
        if (parts.Length >= 4)
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
