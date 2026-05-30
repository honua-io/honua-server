// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Evaluates MapLibre style expressions against feature properties.
/// Supports common expression operators: get, case, match, interpolate, step, literal, has, !, ==, !=, &lt;, &gt;, etc.
/// </summary>
internal static class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates a MapLibre expression to a color value.
    /// </summary>
    public static SKColor EvaluateColor(MapLibreExpression expression, ImmutableDictionary<string, object?> properties)
    {
        var result = Evaluate(expression, properties);
        return ParseColor(result);
    }

    /// <summary>
    /// Evaluates a MapLibre expression to a float value.
    /// </summary>
    public static float EvaluateFloat(MapLibreExpression expression, ImmutableDictionary<string, object?> properties, float defaultValue = 0f)
    {
        var result = Evaluate(expression, properties);
        return ConvertToFloat(result, defaultValue);
    }

    /// <summary>
    /// Evaluates a MapLibre expression to a string value.
    /// </summary>
    public static string? EvaluateString(MapLibreExpression expression, ImmutableDictionary<string, object?> properties)
    {
        var result = Evaluate(expression, properties);
        return result?.ToString();
    }

    /// <summary>
    /// Core expression evaluator.
    /// </summary>
    public static object? Evaluate(MapLibreExpression expression, ImmutableDictionary<string, object?> properties)
    {
        return expression.Kind switch
        {
            MapLibreExpressionKind.String => expression.StringValue,
            MapLibreExpressionKind.Number => expression.NumberValue,
            MapLibreExpressionKind.Boolean => expression.BoolValue,
            MapLibreExpressionKind.Null => null,
            MapLibreExpressionKind.Array => expression.Items is { Length: > 0 }
                ? EvaluateArrayExpression(expression.Items, properties)
                : null,
            _ => null
        };
    }

    internal static bool TryGetNumberArray(MapLibreExpression expression, out float[] values)
    {
        values = [];
        if (expression.Kind != MapLibreExpressionKind.Array || expression.Items is not { Length: > 0 })
        {
            return false;
        }

        var items = expression.Items;
        if (TryGetString(items[0], out var op) && string.Equals(op, "literal", StringComparison.OrdinalIgnoreCase))
        {
            if (items.Length < 2)
            {
                return false;
            }

            return TryGetNumberArray(items[1], out values);
        }

        var list = new List<float>(items.Length);
        foreach (var item in items)
        {
            if (item.Kind != MapLibreExpressionKind.Number)
            {
                values = [];
                return false;
            }

            list.Add((float)item.NumberValue);
        }

        values = list.Count > 0 ? [.. list] : [];
        return values.Length > 0;
    }

    private static object? EvaluateArrayExpression(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length == 0)
        {
            return null;
        }

        if (!TryGetString(array[0], out var op) || op == null)
        {
            return null;
        }

        return op switch
        {
            "get" => EvaluateGet(array, properties),
            "has" => EvaluateHas(array, properties),
            "!" => EvaluateNot(array, properties),
            "case" => EvaluateCase(array, properties),
            "match" => EvaluateMatch(array, properties),
            "step" => EvaluateStep(array, properties),
            "interpolate" => EvaluateInterpolate(array, properties),
            "literal" => array.Length > 1 ? EvaluateLiteral(array[1]) : null,
            "to-string" => EvaluateToString(array, properties),
            "to-number" => EvaluateToNumber(array, properties),
            "typeof" => EvaluateTypeof(array, properties),
            "concat" => EvaluateConcat(array, properties),
            "==" => EvaluateComparison(array, properties, CompareEqual),
            "!=" => EvaluateComparison(array, properties, CompareNotEqual),
            "<" => EvaluateComparison(array, properties, CompareLessThan),
            ">" => EvaluateComparison(array, properties, CompareGreaterThan),
            "<=" => EvaluateComparison(array, properties, CompareLessThanOrEqual),
            ">=" => EvaluateComparison(array, properties, CompareGreaterThanOrEqual),
            "all" => EvaluateAll(array, properties),
            "any" => EvaluateAny(array, properties),
            "coalesce" => EvaluateCoalesce(array, properties),
            "+" => EvaluateArithmetic(array, properties, (a, b) => a + b),
            "-" => EvaluateArithmetic(array, properties, (a, b) => a - b),
            "*" => EvaluateArithmetic(array, properties, (a, b) => a * b),
            "/" => EvaluateArithmetic(array, properties, (a, b) => b != 0 ? a / b : 0),
            _ => null
        };
    }

    private static object? EvaluateLiteral(MapLibreExpression expression)
    {
        return expression.Kind switch
        {
            MapLibreExpressionKind.String => expression.StringValue,
            MapLibreExpressionKind.Number => expression.NumberValue,
            MapLibreExpressionKind.Boolean => expression.BoolValue,
            MapLibreExpressionKind.Null => null,
            MapLibreExpressionKind.Array => expression.Items is { Length: > 0 }
                ? expression.Items.Select(EvaluateLiteral).ToArray()
                : Array.Empty<object?>(),
            _ => null
        };
    }

    private static object? EvaluateGet(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return null;
        }

        if (!TryGetString(array[1], out var key) || key == null)
        {
            return null;
        }

        return properties.TryGetValue(key, out var value) ? value : null;
    }

    private static bool EvaluateHas(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return false;
        }

        if (!TryGetString(array[1], out var key) || key == null)
        {
            return false;
        }

        return properties.ContainsKey(key);
    }

    private static bool EvaluateNot(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return true;
        }

        var result = Evaluate(array[1], properties);
        return !IsTruthy(result);
    }

    private static object? EvaluateCase(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        // case, condition1, output1, condition2, output2, ..., fallback
        for (int i = 1; i < length - 1; i += 2)
        {
            var condition = Evaluate(array[i], properties);
            if (IsTruthy(condition))
            {
                return Evaluate(array[i + 1], properties);
            }
        }

        // Return fallback (last element)
        return length > 1 ? Evaluate(array[length - 1], properties) : null;
    }

    private static object? EvaluateMatch(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        if (length < 4)
        {
            return null;
        }

        var input = Evaluate(array[1], properties);
        var inputStr = input?.ToString();

        // match, input, label1, output1, label2, output2, ..., fallback
        for (int i = 2; i < length - 1; i += 2)
        {
            var label = array[i];
            if (label.Kind == MapLibreExpressionKind.Array && label.Items is { Length: > 0 })
            {
                foreach (var item in label.Items)
                {
                    if (MatchesLabel(inputStr, input, item))
                    {
                        return Evaluate(array[i + 1], properties);
                    }
                }
            }
            else if (MatchesLabel(inputStr, input, label))
            {
                return Evaluate(array[i + 1], properties);
            }
        }

        // Fallback
        return Evaluate(array[length - 1], properties);
    }

    private static bool MatchesLabel(string? inputStr, object? inputObj, MapLibreExpression label)
    {
        if (label.Kind == MapLibreExpressionKind.Array && label.Items is { Length: > 0 })
        {
            foreach (var item in label.Items)
            {
                if (MatchesLabel(inputStr, inputObj, item))
                {
                    return true;
                }
            }

            return false;
        }

        switch (label.Kind)
        {
            case MapLibreExpressionKind.String:
                return inputStr != null && string.Equals(inputStr, label.StringValue, StringComparison.Ordinal);
            case MapLibreExpressionKind.Number:
                var labelValue = label.NumberValue;
                return inputObj switch
                {
                    double d => Math.Abs(d - labelValue) < 0.0001,
                    float f => Math.Abs(f - labelValue) < 0.0001,
                    int i => Math.Abs(i - labelValue) < 0.0001,
                    long l => Math.Abs(l - labelValue) < 0.0001,
                    _ => inputStr != null &&
                         double.TryParse(inputStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                         Math.Abs(parsed - labelValue) < 0.0001
                };
            case MapLibreExpressionKind.Boolean:
                return inputObj switch
                {
                    bool b => b == label.BoolValue,
                    _ => inputStr != null && bool.TryParse(inputStr, out var parsedBool) && parsedBool == label.BoolValue
                };
            case MapLibreExpressionKind.Null:
                return inputObj is null;
            default:
                return false;
        }
    }

    private static object? EvaluateStep(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        if (length < 4)
        {
            return null;
        }

        var input = ConvertToFloat(Evaluate(array[1], properties), 0f);
        var defaultOutput = Evaluate(array[2], properties);

        // step, input, default, stop1, output1, stop2, output2, ...
        object? result = defaultOutput;
        for (int i = 3; i < length - 1; i += 2)
        {
            var stop = ConvertToFloat(Evaluate(array[i], properties), float.MaxValue);
            if (input >= stop)
            {
                result = Evaluate(array[i + 1], properties);
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static object? EvaluateInterpolate(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        if (length < 5)
        {
            return null;
        }

        // interpolate, ["linear"], input, stop1, output1, stop2, output2, ...
        var input = ConvertToFloat(Evaluate(array[2], properties), 0f);

        float? prevStop = null;
        object? prevOutput = null;

        for (int i = 3; i < length - 1; i += 2)
        {
            var stop = ConvertToFloat(Evaluate(array[i], properties), 0f);
            var output = Evaluate(array[i + 1], properties);

            if (input <= stop)
            {
                if (prevStop == null)
                {
                    return output;
                }

                var t = (input - prevStop.Value) / (stop - prevStop.Value);
                return InterpolateValues(prevOutput, output, t);
            }

            prevStop = stop;
            prevOutput = output;
        }

        return prevOutput;
    }

    private static object? InterpolateValues(object? from, object? to, float t)
    {
        var fromF = ConvertToFloat(from, 0f);
        var toF = ConvertToFloat(to, 0f);
        return (double)(fromF + (toF - fromF) * t);
    }

    private static string EvaluateToString(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return "";
        }

        return Evaluate(array[1], properties)?.ToString() ?? "";
    }

    private static object? EvaluateToNumber(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return 0.0;
        }

        var val = Evaluate(array[1], properties);
        return (double)ConvertToFloat(val, 0f);
    }

    /// <summary>
    /// Evaluates the MapLibre <c>typeof</c> expression, returning the runtime type name
    /// of the evaluated value: "number", "string", "boolean", "object", or "null".
    /// </summary>
    private static string EvaluateTypeof(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        if (array.Length < 2)
        {
            return "null";
        }

        var val = Evaluate(array[1], properties);
        return val switch
        {
            null => "null",
            bool => "boolean",
            int or long or float or double or decimal => "number",
            string => "string",
            _ => "object"
        };
    }

    private static string EvaluateConcat(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        var parts = new string[length - 1];
        for (int i = 1; i < length; i++)
        {
            parts[i - 1] = Evaluate(array[i], properties)?.ToString() ?? "";
        }

        return string.Concat(parts);
    }

    private static bool EvaluateComparison(
        MapLibreExpression[] array,
        ImmutableDictionary<string, object?> properties,
        Func<object?, object?, bool> comparator)
    {
        if (array.Length < 3)
        {
            return false;
        }

        var left = Evaluate(array[1], properties);
        var right = Evaluate(array[2], properties);
        return comparator(left, right);
    }

    private static bool EvaluateAll(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            if (!IsTruthy(Evaluate(array[i], properties)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EvaluateAny(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            if (IsTruthy(Evaluate(array[i], properties)))
            {
                return true;
            }
        }

        return false;
    }

    private static object? EvaluateCoalesce(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            var val = Evaluate(array[i], properties);
            if (val != null)
            {
                return val;
            }
        }

        return null;
    }

    private static double EvaluateArithmetic(
        MapLibreExpression[] array,
        ImmutableDictionary<string, object?> properties,
        Func<double, double, double> op)
    {
        if (array.Length < 3)
        {
            return 0.0;
        }

        var left = ConvertToFloat(Evaluate(array[1], properties), 0f);
        var right = ConvertToFloat(Evaluate(array[2], properties), 0f);
        return op(left, right);
    }

    private static bool CompareEqual(object? a, object? b) =>
        string.Equals(a?.ToString(), b?.ToString(), StringComparison.Ordinal);

    private static bool CompareNotEqual(object? a, object? b) => !CompareEqual(a, b);

    private static bool CompareLessThan(object? a, object? b)
    {
        var af = ConvertToFloat(a, 0f);
        var bf = ConvertToFloat(b, 0f);
        return af < bf;
    }

    private static bool CompareGreaterThan(object? a, object? b) => CompareLessThan(b, a);

    private static bool CompareLessThanOrEqual(object? a, object? b) => !CompareGreaterThan(a, b);

    private static bool CompareGreaterThanOrEqual(object? a, object? b) => !CompareLessThan(a, b);

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            null => false,
            bool b => b,
            double d => d != 0.0,
            string s => s.Length > 0,
            _ => true
        };
    }

    private static bool TryGetString(MapLibreExpression expression, out string? value)
    {
        if (expression.Kind == MapLibreExpressionKind.String)
        {
            value = expression.StringValue;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Parses a color from various formats (hex, rgb, rgba, named colors).
    /// </summary>
    internal static SKColor ParseColor(object? value)
    {
        if (value == null)
        {
            return SKColors.Transparent;
        }

        var str = value.ToString();
        if (string.IsNullOrEmpty(str))
        {
            return SKColors.Transparent;
        }

        // Handle hex colors
        if (str.StartsWith('#'))
        {
            if (SKColor.TryParse(str, out var hex))
            {
                return hex;
            }
        }

        // Handle rgb(r,g,b) and rgba(r,g,b,a)
        if (str.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return ParseRgbColor(str);
        }

        // Handle hsl(h,s,l) and hsla(h,s,l,a)
        if (str.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return ParseHslColor(str);
        }

        // Named colors
        return str.ToLowerInvariant() switch
        {
            "transparent" => SKColors.Transparent,
            "black" => SKColors.Black,
            "white" => SKColors.White,
            "red" => SKColors.Red,
            "green" => new SKColor(0, 128, 0),
            "blue" => SKColors.Blue,
            "yellow" => SKColors.Yellow,
            "orange" => new SKColor(255, 165, 0),
            "purple" => new SKColor(128, 0, 128),
            "gray" or "grey" => SKColors.Gray,
            _ => SKColor.TryParse(str, out var parsed) ? parsed : SKColors.Black
        };
    }

    private static SKColor ParseRgbColor(string value)
    {
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start < 0 || end < 0)
        {
            return SKColors.Black;
        }

        var parts = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return SKColors.Black;
        }

        if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            return SKColors.Black;
        }

        byte a = 255;
        if (parts.Length >= 4 &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        }

        return new SKColor(r, g, b, a);
    }

    private static SKColor ParseHslColor(string value)
    {
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start < 0 || end < 0)
        {
            return SKColors.Black;
        }

        var parts = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return SKColors.Black;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !float.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !float.TryParse(parts[2].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
        {
            return SKColors.Black;
        }

        byte a = 255;
        if (parts.Length >= 4 &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        }

        // HSL to RGB conversion
        s /= 100f;
        l /= 100f;
        var (r, g, b) = HslToRgb(h, s, l);
        return new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), a);
    }

    private static (float R, float G, float B) HslToRgb(float h, float s, float l)
    {
        h /= 360f;
        if (s == 0f)
        {
            return (l, l, l);
        }

        var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        var p = 2f * l - q;
        return (HueToRgb(p, q, h + 1f / 3f), HueToRgb(p, q, h), HueToRgb(p, q, h - 1f / 3f));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f)
        {
            t += 1f;
        }

        if (t > 1f)
        {
            t -= 1f;
        }

        if (t < 1f / 6f)
        {
            return p + (q - p) * 6f * t;
        }

        if (t < 1f / 2f)
        {
            return q;
        }

        if (t < 2f / 3f)
        {
            return p + (q - p) * (2f / 3f - t) * 6f;
        }

        return p;
    }

    internal static float ConvertToFloat(object? value, float defaultValue)
    {
        return value switch
        {
            double d => (float)d,
            float f => f,
            int i => i,
            long lng => lng,
            decimal dec => (float)dec,
            string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => defaultValue
        };
    }
}
