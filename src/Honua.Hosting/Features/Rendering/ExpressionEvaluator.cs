// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SkiaSharp;

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Evaluates MapLibre style expressions against feature properties and the zoom the render is
/// being evaluated at.
/// Supports common expression operators: get, zoom, case, match, interpolate, step, literal, has, !, ==, !=, &lt;, &gt;, etc.
/// </summary>
/// <remarks>
/// Every entry point takes an explicit <see cref="RenderZoom"/> rather than defaulting to "no zoom",
/// so a caller cannot reach the zoom-dependent operators by accident. A render that carries
/// <see cref="RenderZoom.NotDerivable"/> and evaluates a <c>["zoom"]</c> expression raises
/// <see cref="StyleExpressionEvaluationException"/> instead of substituting a placeholder level
/// (honua-server#2873).
/// </remarks>
internal static class ExpressionEvaluator
{
    /// <summary>
    /// Tolerance used in place of exact floating-point equality when evaluating
    /// numeric MapLibre style expressions (division-by-zero guards, truthiness,
    /// and HSL color math), since style values are frequently the product of
    /// upstream arithmetic rather than exact literals.
    /// </summary>
    private const double NumericEpsilon = 1e-9;

    /// <summary>
    /// Evaluates a MapLibre expression to a color value at the supplied <paramref name="zoom"/>.
    /// </summary>
    public static SKColor EvaluateColor(
        MapLibreExpression expression,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        var result = Evaluate(expression, properties, zoom);
        return ParseColor(result);
    }

    /// <summary>
    /// Evaluates a MapLibre expression to a float value at the supplied <paramref name="zoom"/>.
    /// </summary>
    public static float EvaluateFloat(
        MapLibreExpression expression,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom,
        float defaultValue = 0f)
    {
        var result = Evaluate(expression, properties, zoom);
        return ConvertToFloat(result, defaultValue);
    }

    /// <summary>
    /// Evaluates a MapLibre expression to a string value at the supplied <paramref name="zoom"/>.
    /// </summary>
    public static string? EvaluateString(
        MapLibreExpression expression,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        var result = Evaluate(expression, properties, zoom);
        return result?.ToString();
    }

    /// <summary>
    /// Core expression evaluator.
    /// </summary>
    /// <exception cref="StyleExpressionEvaluationException">
    /// The expression evaluates <c>["zoom"]</c> but <paramref name="zoom"/> carries no derived level.
    /// </exception>
    public static object? Evaluate(
        MapLibreExpression expression,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
    {
        ArgumentNullException.ThrowIfNull(zoom);

        return expression.Kind switch
        {
            MapLibreExpressionKind.String => expression.StringValue,
            MapLibreExpressionKind.Number => expression.NumberValue,
            MapLibreExpressionKind.Boolean => expression.BoolValue,
            MapLibreExpressionKind.Null => null,
            MapLibreExpressionKind.Array => expression.Items is { Length: > 0 }
                ? EvaluateArrayExpression(expression.Items, properties, zoom)
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

    private static object? EvaluateArrayExpression(
        MapLibreExpression[] array,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom)
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
            "zoom" => EvaluateZoom(zoom),
            "!" => EvaluateNot(array, properties, zoom),
            "case" => EvaluateCase(array, properties, zoom),
            "match" => EvaluateMatch(array, properties, zoom),
            "step" => EvaluateStep(array, properties, zoom),
            "interpolate" => EvaluateInterpolate(array, properties, zoom),
            "literal" => array.Length > 1 ? EvaluateLiteral(array[1]) : null,
            "to-string" => EvaluateToString(array, properties, zoom),
            "to-number" => EvaluateToNumber(array, properties, zoom),
            "typeof" => EvaluateTypeof(array, properties, zoom),
            "concat" => EvaluateConcat(array, properties, zoom),
            "==" => EvaluateComparison(array, properties, zoom, CompareEqual),
            "!=" => EvaluateComparison(array, properties, zoom, CompareNotEqual),
            "<" => EvaluateComparison(array, properties, zoom, CompareLessThan),
            ">" => EvaluateComparison(array, properties, zoom, CompareGreaterThan),
            "<=" => EvaluateComparison(array, properties, zoom, CompareLessThanOrEqual),
            ">=" => EvaluateComparison(array, properties, zoom, CompareGreaterThanOrEqual),
            "all" => EvaluateAll(array, properties, zoom),
            "any" => EvaluateAny(array, properties, zoom),
            "coalesce" => EvaluateCoalesce(array, properties, zoom),
            "+" => EvaluateArithmetic(array, properties, zoom, (a, b) => a + b),
            "-" => EvaluateArithmetic(array, properties, zoom, (a, b) => a - b),
            "*" => EvaluateArithmetic(array, properties, zoom, (a, b) => a * b),
            "/" => EvaluateArithmetic(array, properties, zoom, (a, b) => Math.Abs(b) > NumericEpsilon ? a / b : 0),
            _ => null
        };
    }

    /// <summary>
    /// Evaluates the MapLibre <c>["zoom"]</c> input, which yields the zoom level the render is being
    /// evaluated at. MapLibre GL JS reads this from the evaluation globals
    /// (<c>expression/definitions/index.ts</c> binds <c>zoom</c> to <c>ctx.globals.zoom</c>) and
    /// returns it as a plain number, so a fractional zoom stays fractional here too.
    /// </summary>
    /// <remarks>
    /// When the render carries no derived zoom this raises rather than substituting a level. A
    /// substituted zoom is not a degraded picture but a confidently wrong one: every zoom ramp would
    /// silently collapse onto whichever stop the placeholder selects, with no throw, warning, or log
    /// — the failure mode that made <c>interpolate</c> render black in honua-server#2867 and that let
    /// <c>minzoom</c>/<c>maxzoom</c> be skipped in honua-server#2868. The
    /// <see cref="RenderZoom.NotDerivableReason"/> recorded by the render path is carried into the
    /// message so the cause is traceable from the failure alone.
    /// </remarks>
    private static double EvaluateZoom(RenderZoom zoom)
    {
        if (zoom.Level is { } level)
        {
            return level;
        }

        throw new StyleExpressionEvaluationException(
            "Cannot evaluate a [\"zoom\"] expression: no zoom could be derived for this render because "
            + $"{zoom.NotDerivableReason}. The style is zoom-dependent, so it cannot be rendered "
            + "correctly at an unknown zoom.");
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

    private static bool EvaluateNot(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return true;
        }

        var result = Evaluate(array[1], properties, zoom);
        return !IsTruthy(result);
    }

    private static object? EvaluateCase(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        // case, condition1, output1, condition2, output2, ..., fallback
        for (int i = 1; i < length - 1; i += 2)
        {
            var condition = Evaluate(array[i], properties, zoom);
            if (IsTruthy(condition))
            {
                return Evaluate(array[i + 1], properties, zoom);
            }
        }

        // Return fallback (last element)
        return length > 1 ? Evaluate(array[length - 1], properties, zoom) : null;
    }

    private static object? EvaluateMatch(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        if (length < 4)
        {
            return null;
        }

        var input = Evaluate(array[1], properties, zoom);
        var inputStr = input?.ToString();

        // match, input, label1, output1, label2, output2, ..., fallback
        for (int i = 2; i < length - 1; i += 2)
        {
            var label = array[i];
            if (label.Kind == MapLibreExpressionKind.Array && label.Items is { Length: > 0 })
            {
                if (MatchesAnyLabel(inputStr, input, label.Items))
                {
                    return Evaluate(array[i + 1], properties, zoom);
                }
            }
            else if (MatchesLabel(inputStr, input, label))
            {
                return Evaluate(array[i + 1], properties, zoom);
            }
        }

        // Fallback
        return Evaluate(array[length - 1], properties, zoom);
    }

    private static bool MatchesLabel(string? inputStr, object? inputObj, MapLibreExpression label)
    {
        if (label.Kind == MapLibreExpressionKind.Array && label.Items is { Length: > 0 })
        {
            return MatchesAnyLabel(inputStr, inputObj, label.Items);
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

    private static bool MatchesAnyLabel(
        string? inputStr,
        object? inputObj,
        MapLibreExpression[] labels)
    {
        var index = 0;
        while (index < labels.Length)
        {
            if (MatchesLabel(inputStr, inputObj, labels[index]))
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static object? EvaluateStep(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        if (length < 4)
        {
            return null;
        }

        var input = ConvertToFloat(Evaluate(array[1], properties, zoom), 0f);
        var defaultOutput = Evaluate(array[2], properties, zoom);

        // step, input, default, stop1, output1, stop2, output2, ...
        object? result = defaultOutput;
        for (int i = 3; i < length - 1; i += 2)
        {
            var stop = ConvertToFloat(Evaluate(array[i], properties, zoom), float.MaxValue);
            if (input >= stop)
            {
                result = Evaluate(array[i + 1], properties, zoom);
            }
            else
            {
                break;
            }
        }

        return result;
    }

    private static object? EvaluateInterpolate(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        if (length < 5)
        {
            return null;
        }

        // interpolate, <interpolation>, input, stop1, output1, stop2, output2, ...
        // <interpolation> is ["linear"], ["exponential", base], or ["cubic-bezier", x1, y1, x2, y2].
        var interpolation = ParseInterpolation(array[1]);
        var input = ConvertToFloat(Evaluate(array[2], properties, zoom), 0f);

        float? prevStop = null;
        object? prevOutput = null;

        for (int i = 3; i < length - 1; i += 2)
        {
            var stop = ConvertToFloat(Evaluate(array[i], properties, zoom), 0f);
            var output = Evaluate(array[i + 1], properties, zoom);

            if (input <= stop)
            {
                if (prevStop == null)
                {
                    return output;
                }

                var t = InterpolationFactor(interpolation, input, prevStop.Value, stop);
                return InterpolateValues(prevOutput, output, t);
            }

            prevStop = stop;
            prevOutput = output;
        }

        return prevOutput;
    }

    /// <summary>
    /// The interpolation curve declared as the second operand of a MapLibre
    /// <c>interpolate</c> expression. Selects how the fractional position between two
    /// consecutive stops is computed before the stop outputs are blended.
    /// </summary>
    private enum InterpolationKind
    {
        Linear,
        Exponential,
        CubicBezier
    }

    private readonly record struct InterpolationCurve(
        InterpolationKind Kind,
        double Base,
        double X1,
        double Y1,
        double X2,
        double Y2)
    {
        public static readonly InterpolationCurve Linear = new(InterpolationKind.Linear, 1.0, 0, 0, 0, 0);
    }

    /// <summary>
    /// Reads the <c>interpolate</c> curve operand (<c>["linear"]</c>,
    /// <c>["exponential", base]</c>, or <c>["cubic-bezier", x1, y1, x2, y2]</c>). Anything
    /// unrecognized falls back to <see cref="InterpolationKind.Linear"/>, matching the
    /// evaluator's historical behavior of treating the operand as linear.
    /// </summary>
    private static InterpolationCurve ParseInterpolation(MapLibreExpression operand)
    {
        if (operand.Kind != MapLibreExpressionKind.Array || operand.Items is not { Length: > 0 } items)
        {
            return InterpolationCurve.Linear;
        }

        if (!TryGetString(items[0], out var name) || name == null)
        {
            return InterpolationCurve.Linear;
        }

        if (string.Equals(name, "exponential", StringComparison.Ordinal))
        {
            if (items.Length >= 2 && items[1].Kind == MapLibreExpressionKind.Number)
            {
                return new InterpolationCurve(InterpolationKind.Exponential, items[1].NumberValue, 0, 0, 0, 0);
            }

            return InterpolationCurve.Linear;
        }

        if (string.Equals(name, "cubic-bezier", StringComparison.Ordinal))
        {
            if (items.Length >= 5 &&
                items[1].Kind == MapLibreExpressionKind.Number &&
                items[2].Kind == MapLibreExpressionKind.Number &&
                items[3].Kind == MapLibreExpressionKind.Number &&
                items[4].Kind == MapLibreExpressionKind.Number)
            {
                return new InterpolationCurve(
                    InterpolationKind.CubicBezier,
                    1.0,
                    items[1].NumberValue,
                    items[2].NumberValue,
                    items[3].NumberValue,
                    items[4].NumberValue);
            }

            return InterpolationCurve.Linear;
        }

        return InterpolationCurve.Linear;
    }

    /// <summary>
    /// Computes the interpolation factor <c>t</c> for <paramref name="input"/> between two
    /// adjacent stop inputs, honoring the declared curve. Mirrors MapLibre GL JS's
    /// <c>Interpolate.interpolationFactor</c> in
    /// <c>maplibre-style-spec/src/expression/definitions/interpolate.ts</c>. The
    /// <see cref="InterpolationKind.Linear"/> branch is deliberately the same
    /// single-precision expression the evaluator has always used, so existing linear ramps
    /// stay bit-for-bit unchanged; the non-linear curves compute in double precision to
    /// match MapLibre's <c>Math.pow</c>/unit-bezier math.
    /// </summary>
    private static float InterpolationFactor(in InterpolationCurve curve, float input, float lower, float upper) =>
        curve.Kind switch
        {
            InterpolationKind.Exponential => (float)ExponentialInterpolation(input, curve.Base, lower, upper),
            InterpolationKind.CubicBezier =>
                (float)SolveCubicBezier(curve, ExponentialInterpolation(input, 1.0, lower, upper)),
            _ => (input - lower) / (upper - lower),
        };

    /// <summary>
    /// The ratio used to interpolate between two exponential-function stops, ported verbatim
    /// from MapLibre's <c>exponentialInterpolation</c>. <c>base == 1</c> collapses to the
    /// linear ratio <c>progress / difference</c> (MapLibre's own degenerate case), so an
    /// <c>["exponential", 1]</c> curve is identical to <c>["linear"]</c>. The exponents are
    /// the raw stop-relative progress and difference (not a pre-normalized 0..1 factor):
    /// <c>(base^progress - 1) / (base^difference - 1)</c>.
    /// </summary>
    private static double ExponentialInterpolation(double input, double @base, double lower, double upper)
    {
        var difference = upper - lower;
        var progress = input - lower;

        if (difference.Equals(0d))
        {
            return 0.0;
        }

        if (@base.Equals(1d))
        {
            return progress / difference;
        }

        return (Math.Pow(@base, progress) - 1.0) / (Math.Pow(@base, difference) - 1.0);
    }

    /// <summary>
    /// Solves the unit cubic Bézier <c>y</c> for a given <c>x</c> using Newton-Raphson with a
    /// bisection fallback, a verbatim port of MapLibre's <c>UnitBezier.solve</c>
    /// (<c>@mapbox/unitbezier</c>). The cubic-bezier curve feeds the linear stop factor in as
    /// <c>x</c> and returns the eased factor as <c>y</c>.
    /// </summary>
    private static double SolveCubicBezier(in InterpolationCurve curve, double x)
    {
        const double epsilon = 1e-6;

        var cx = 3 * curve.X1;
        var bx = 3 * (curve.X2 - curve.X1) - cx;
        var ax = 1 - cx - bx;
        var cy = 3 * curve.Y1;
        var by = 3 * (curve.Y2 - curve.Y1) - cy;
        var ay = 1 - cy - by;

        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        var t = x;
        for (int i = 0; i < 8; i++)
        {
            var x2 = ((ax * t + bx) * t + cx) * t - x;
            if (Math.Abs(x2) < epsilon)
            {
                return ((ay * t + by) * t + cy) * t;
            }

            var d2 = (3 * ax * t + 2 * bx) * t + cx;
            if (Math.Abs(d2) < 1e-6)
            {
                break;
            }

            t -= x2 / d2;
        }

        double t0 = 0;
        double t1 = 1;
        t = x;
        for (int i = 0; i < 20; i++)
        {
            var x2 = ((ax * t + bx) * t + cx) * t;
            if (Math.Abs(x2 - x) < epsilon)
            {
                break;
            }

            if (x > x2)
            {
                t0 = t;
            }
            else
            {
                t1 = t;
            }

            t = (t0 + t1) * 0.5;
        }

        return ((ay * t + by) * t + cy) * t;
    }

    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "Returns a boxed double for numeric stops and an SKColor for color stops; the caller dispatches on the runtime type.")]
    private static object InterpolateValues(object? from, object? to, float t)
    {
        if (TryConvertToFloat(from, out var fromF) && TryConvertToFloat(to, out var toF))
        {
            return (double)(fromF + (toF - fromF) * t);
        }

        if (TryParseColor(from, out var fromColor) && TryParseColor(to, out var toColor))
        {
            return InterpolateColors(fromColor, toColor, t);
        }

        throw new StyleExpressionEvaluationException(
            $"Cannot interpolate between stop outputs of type '{DescribeStopType(from)}' and "
            + $"'{DescribeStopType(to)}'. MapLibre 'interpolate' requires every stop output to be "
            + "the same interpolatable type (all numbers or all colors).");
    }

    /// <summary>
    /// Interpolates two colors the way MapLibre GL JS does for the default <c>rgb</c>
    /// interpolation space: a straight (non-premultiplied) per-channel lerp over
    /// gamma-encoded sRGB, including alpha. Mirrors <c>Color.interpolate</c> in
    /// <c>maplibre-style-spec/src/expression/types/color.ts</c>, which reads the
    /// un-premultiplied <c>rgb</c> getter and applies <c>interpolateNumber</c>
    /// (<c>from + t * (to - from)</c>) to each channel. Deliberately not a linear-light
    /// blend — MapLibre reserves perceptual spaces for 'interpolate-hcl'/'interpolate-lab'.
    /// </summary>
    private static SKColor InterpolateColors(SKColor from, SKColor to, float t) =>
        new(
            InterpolateChannel(from.Red, to.Red, t),
            InterpolateChannel(from.Green, to.Green, t),
            InterpolateChannel(from.Blue, to.Blue, t),
            InterpolateChannel(from.Alpha, to.Alpha, t));

    /// <summary>
    /// Lerps a single 8-bit channel. The arithmetic runs in normalized 0..1 double
    /// precision — rather than directly over the byte values — because MapLibre holds
    /// channels as 0..1 float64 and only quantizes to 8 bits at raster time. The two
    /// disagree by one step wherever byte-space math lands exactly on .5 but the
    /// normalized math does not: interpolating 251 and 48 at t=0.5 is exactly 149.5
    /// over bytes (rounding to 150), yet 149.4999... over 0..1 (rounding to 149, which
    /// is what MapLibre emits).
    /// </summary>
    private static byte InterpolateChannel(byte from, byte to, float t)
    {
        var fromN = from / 255.0;
        var toN = to / 255.0;
        var value = (fromN + t * (toN - fromN)) * 255.0;

        // Channels stay within 0..255 for t in [0,1]; clamp guards against a caller
        // extrapolating outside the stop range.
        return (byte)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0.0, 255.0);
    }

    private static string DescribeStopType(object? value) =>
        value switch
        {
            null => "null",
            bool => "boolean",
            int or long or float or double or decimal => "number",
            SKColor => "color",
            string s => TryParseColor(s, out _) ? "color" : "string",
            _ => "object"
        };

    private static string EvaluateToString(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return "";
        }

        return Evaluate(array[1], properties, zoom)?.ToString() ?? "";
    }

    private static object? EvaluateToNumber(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return 0.0;
        }

        var val = Evaluate(array[1], properties, zoom);
        return (double)ConvertToFloat(val, 0f);
    }

    /// <summary>
    /// Evaluates the MapLibre <c>typeof</c> expression, returning the runtime type name
    /// of the evaluated value: "number", "string", "boolean", "object", or "null".
    /// </summary>
    private static string EvaluateTypeof(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        if (array.Length < 2)
        {
            return "null";
        }

        var val = Evaluate(array[1], properties, zoom);
        return val switch
        {
            null => "null",
            bool => "boolean",
            int or long or float or double or decimal => "number",
            string => "string",
            _ => "object"
        };
    }

    private static string EvaluateConcat(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        var parts = new string[length - 1];
        for (int i = 1; i < length; i++)
        {
            parts[i - 1] = Evaluate(array[i], properties, zoom)?.ToString() ?? "";
        }

        return string.Concat(parts);
    }

    private static bool EvaluateComparison(
        MapLibreExpression[] array,
        ImmutableDictionary<string, object?> properties,
        RenderZoom zoom,
        Func<object?, object?, bool> comparator)
    {
        if (array.Length < 3)
        {
            return false;
        }

        var left = Evaluate(array[1], properties, zoom);
        var right = Evaluate(array[2], properties, zoom);
        return comparator(left, right);
    }

    private static bool EvaluateAll(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            if (!IsTruthy(Evaluate(array[i], properties, zoom)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EvaluateAny(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            if (IsTruthy(Evaluate(array[i], properties, zoom)))
            {
                return true;
            }
        }

        return false;
    }

    private static object? EvaluateCoalesce(MapLibreExpression[] array, ImmutableDictionary<string, object?> properties, RenderZoom zoom)
    {
        var length = array.Length;
        for (int i = 1; i < length; i++)
        {
            var val = Evaluate(array[i], properties, zoom);
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
        RenderZoom zoom,
        Func<double, double, double> op)
    {
        if (array.Length < 3)
        {
            return 0.0;
        }

        var left = ConvertToFloat(Evaluate(array[1], properties, zoom), 0f);
        var right = ConvertToFloat(Evaluate(array[2], properties, zoom), 0f);
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
            double d => Math.Abs(d) > NumericEpsilon,
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
    /// Parses a color from various formats (hex, rgb, rgba, named colors),
    /// falling back to transparent for absent values and black for unrecognized ones.
    /// </summary>
    internal static SKColor ParseColor(object? value)
    {
        if (TryParseColor(value, out var color))
        {
            return color;
        }

        return string.IsNullOrEmpty(value?.ToString()) ? SKColors.Transparent : SKColors.Black;
    }

    /// <summary>
    /// Attempts to parse a color from various formats (hex, rgb, rgba, hsl, named colors),
    /// reporting failure instead of substituting a fallback. Callers that must distinguish
    /// "not a color" from "black" — such as interpolation stop typing — use this overload.
    /// </summary>
    internal static bool TryParseColor(object? value, out SKColor color)
    {
        color = default;
        if (value is SKColor already)
        {
            color = already;
            return true;
        }

        var str = value?.ToString();
        if (string.IsNullOrEmpty(str))
        {
            return false;
        }

        // Handle hex colors
        if (str.StartsWith('#'))
        {
            return SKColor.TryParse(str, out color);
        }

        // Handle rgb(r,g,b) and rgba(r,g,b,a)
        if (str.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbColor(str, out color);
        }

        // Handle hsl(h,s,l) and hsla(h,s,l,a)
        if (str.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHslColor(str, out color);
        }

        // Named colors
        switch (str.ToLowerInvariant())
        {
            case "transparent":
                color = SKColors.Transparent;
                return true;
            case "black":
                color = SKColors.Black;
                return true;
            case "white":
                color = SKColors.White;
                return true;
            case "red":
                color = SKColors.Red;
                return true;
            case "green":
                color = new SKColor(0, 128, 0);
                return true;
            case "blue":
                color = SKColors.Blue;
                return true;
            case "yellow":
                color = SKColors.Yellow;
                return true;
            case "orange":
                color = new SKColor(255, 165, 0);
                return true;
            case "purple":
                color = new SKColor(128, 0, 128);
                return true;
            case "gray":
            case "grey":
                color = SKColors.Gray;
                return true;
            default:
                return SKColor.TryParse(str, out color);
        }
    }

    private static bool TryParseRgbColor(string value, out SKColor color)
    {
        color = default;
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start < 0 || end < 0)
        {
            return false;
        }

        var parts = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
        {
            return false;
        }

        byte a = 255;
        if (parts.Length >= 4 &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha))
        {
            a = (byte)Math.Clamp(alpha * 255f, 0f, 255f);
        }

        color = new SKColor(r, g, b, a);
        return true;
    }

    private static bool TryParseHslColor(string value, out SKColor color)
    {
        color = default;
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start < 0 || end < 0)
        {
            return false;
        }

        var parts = value[(start + 1)..end].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return false;
        }

        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ||
            !float.TryParse(parts[1].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ||
            !float.TryParse(parts[2].TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var l))
        {
            return false;
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
        color = new SKColor((byte)(r * 255), (byte)(g * 255), (byte)(b * 255), a);
        return true;
    }

    private static (float R, float G, float B) HslToRgb(float h, float s, float l)
    {
        h /= 360f;
        if (MathF.Abs(s) <= NumericEpsilon)
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

    internal static float ConvertToFloat(object? value, float defaultValue) =>
        TryConvertToFloat(value, out var result) ? result : defaultValue;

    private static bool TryConvertToFloat(object? value, out float result)
    {
        switch (value)
        {
            case double d:
                result = (float)d;
                return true;
            case float f:
                result = f;
                return true;
            case int i:
                result = i;
                return true;
            case long lng:
                result = lng;
                return true;
            case decimal dec:
                result = (float)dec;
                return true;
            case string s when float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0f;
                return false;
        }
    }
}
