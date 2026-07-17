// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using FluentAssertions;
using Honua.Infrastructure.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.Rendering;

/// <summary>
/// Tests for MapLibre expression evaluation.
/// </summary>
[Trait("Component", "MapServer")]
public class ExpressionEvaluatorTests
{
    private static readonly ImmutableDictionary<string, object?> _emptyProps =
        ImmutableDictionary<string, object?>.Empty;

    private static ImmutableDictionary<string, object?> Props(params (string Key, object? Value)[] items) =>
        items.ToImmutableDictionary(i => i.Key, i => i.Value);

    [UnitTest]
    public void Evaluate_GetExpression_ReturnsPropertyValue()
    {
        var expr = MapLibreExpressionParser.Parse("""["get", "name"]""");
        var props = Props(("name", "Test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("Test");
    }

    [UnitTest]
    public void Evaluate_GetExpression_MissingProperty_ReturnsNull()
    {
        var expr = MapLibreExpressionParser.Parse("""["get", "missing"]""");

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_HasExpression_ReturnsTrueWhenPresent()
    {
        var expr = MapLibreExpressionParser.Parse("""["has", "name"]""");
        var props = Props(("name", "Test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_HasExpression_ReturnsFalseWhenMissing()
    {
        var expr = MapLibreExpressionParser.Parse("""["has", "missing"]""");

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_NotExpression_InvertsTruthiness()
    {
        var expr = MapLibreExpressionParser.Parse("""["!", ["has", "name"]]""");

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_EqualityComparison_ReturnsTrue()
    {
        var expr = MapLibreExpressionParser.Parse("""["==", ["get", "type"], "road"]""");
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_EqualityComparison_ReturnsFalse()
    {
        var expr = MapLibreExpressionParser.Parse("""["==", ["get", "type"], "road"]""");
        var props = Props(("type", "building"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_LessThan_ReturnsCorrectResult()
    {
        var expr = MapLibreExpressionParser.Parse("""["<", ["get", "population"], 1000]""");
        var props = Props(("population", 500));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_GreaterThan_ReturnsCorrectResult()
    {
        var expr = MapLibreExpressionParser.Parse(""" [">", ["get", "population"], 1000]""");
        var props = Props(("population", 500));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_AllExpression_ReturnsTrueWhenAllTrue()
    {
        var expr = MapLibreExpressionParser.Parse("""["all", ["has", "name"], ["has", "type"]]""");
        var props = Props(("name", "test"), ("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_AllExpression_ReturnsFalseWhenAnyFalse()
    {
        var expr = MapLibreExpressionParser.Parse("""["all", ["has", "name"], ["has", "missing"]]""");
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_AnyExpression_ReturnsTrueWhenAnyTrue()
    {
        var expr = MapLibreExpressionParser.Parse("""["any", ["has", "missing"], ["has", "name"]]""");
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_MatchExpression_ReturnsMatchedValue()
    {
        var expr = MapLibreExpressionParser.Parse("""["match", ["get", "type"], "road", "blue", "building", "red", "gray"]""");
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("blue");
    }

    [UnitTest]
    public void Evaluate_MatchExpression_ReturnsFallback()
    {
        var expr = MapLibreExpressionParser.Parse("""["match", ["get", "type"], "road", "blue", "building", "red", "gray"]""");
        var props = Props(("type", "park"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("gray");
    }

    [UnitTest]
    public void Evaluate_CaseExpression_ReturnsFirstMatchingBranch()
    {
        var expr = MapLibreExpressionParser.Parse("""["case", ["==", ["get", "type"], "road"], "blue", "gray"]""");
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("blue");
    }

    [UnitTest]
    public void Evaluate_CaseExpression_ReturnsFallback()
    {
        var expr = MapLibreExpressionParser.Parse("""["case", ["==", ["get", "type"], "road"], "blue", "gray"]""");
        var props = Props(("type", "park"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("gray");
    }

    [UnitTest]
    public void Evaluate_ArithmeticAdd_ReturnsSum()
    {
        var expr = MapLibreExpressionParser.Parse("""["+", 10, 20]""");

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().BeOfType<double>().Which.Should().Be(30.0);
    }

    [UnitTest]
    public void Evaluate_CoalesceExpression_ReturnsFirstNonNull()
    {
        var expr = MapLibreExpressionParser.Parse("""["coalesce", ["get", "missing"], ["get", "name"]]""");
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("test");
    }

    [UnitTest]
    public void Evaluate_TypeofExpression_ReturnsNumber()
    {
        var expr = MapLibreExpressionParser.Parse("""["typeof", ["get", "value"]]""");
        var props = Props(("value", 42.0));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("number");
    }

    [UnitTest]
    public void Evaluate_TypeofExpression_ReturnsString()
    {
        var expr = MapLibreExpressionParser.Parse("""["typeof", ["get", "value"]]""");
        var props = Props(("value", "N/A"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("string");
    }

    [UnitTest]
    public void Evaluate_TypeofExpression_ReturnsNull()
    {
        var expr = MapLibreExpressionParser.Parse("""["typeof", ["get", "missing"]]""");

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().Be("null");
    }

    [UnitTest]
    public void Evaluate_TypeofExpression_ReturnsBoolean()
    {
        var expr = MapLibreExpressionParser.Parse("""["typeof", ["get", "flag"]]""");
        var props = Props(("flag", true));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("boolean");
    }

    [UnitTest]
    public void ParseColor_HexColor_ReturnsCorrectColor()
    {
        var color = ExpressionEvaluator.ParseColor("#ff0000");

        color.Red.Should().Be(255);
        color.Green.Should().Be(0);
        color.Blue.Should().Be(0);
    }

    [UnitTest]
    public void ParseColor_ShortHexColor_ReturnsCorrectColor()
    {
        var color = ExpressionEvaluator.ParseColor("#f00");

        color.Red.Should().Be(255);
        color.Green.Should().Be(0);
        color.Blue.Should().Be(0);
    }

    [UnitTest]
    public void ParseColor_RgbFunction_ReturnsCorrectColor()
    {
        var color = ExpressionEvaluator.ParseColor("rgb(128, 64, 32)");

        color.Red.Should().Be(128);
        color.Green.Should().Be(64);
        color.Blue.Should().Be(32);
    }

    [UnitTest]
    public void ParseColor_RgbaFunction_ReturnsCorrectColor()
    {
        var color = ExpressionEvaluator.ParseColor("rgba(255, 0, 0, 0.5)");

        color.Red.Should().Be(255);
        color.Green.Should().Be(0);
        color.Blue.Should().Be(0);
        color.Alpha.Should().BeInRange((byte)126, (byte)129);
    }

    [UnitTest]
    public void ParseColor_NamedColor_ReturnsCorrectColor()
    {
        var color = ExpressionEvaluator.ParseColor("red");

        color.Red.Should().Be(255);
        color.Green.Should().Be(0);
        color.Blue.Should().Be(0);
    }

    [UnitTest]
    public void ParseColor_NullOrEmpty_ReturnsTransparent()
    {
        var color = ExpressionEvaluator.ParseColor(null);

        color.Should().Be(SKColors.Transparent);
    }

    [UnitTest]
    public void EvaluateFloat_NumberValue_ReturnsFloat()
    {
        var expr = MapLibreExpressionParser.Parse("5.0");

        var result = ExpressionEvaluator.EvaluateFloat(expr, _emptyProps, 0f);

        result.Should().Be(5.0f);
    }

    [UnitTest]
    public void ConvertToFloat_DoubleValue_ReturnsFloat()
    {
        var result = ExpressionEvaluator.ConvertToFloat(3.14, 0f);

        result.Should().BeApproximately(3.14f, 0.001f);
    }

    [UnitTest]
    public void ConvertToFloat_StringNumber_ReturnsFloat()
    {
        var result = ExpressionEvaluator.ConvertToFloat("42.5", 0f);

        result.Should().BeApproximately(42.5f, 0.001f);
    }

    [UnitTest]
    public void ConvertToFloat_NonNumericString_ReturnsDefault()
    {
        var result = ExpressionEvaluator.ConvertToFloat("abc", 99f);

        result.Should().Be(99f);
    }

    private const string ChoroplethRamp =
        """["interpolate", ["linear"], ["get", "value"], 0, "#f7fbff", 100, "#08306b"]""";

    private const string MultiStopRamp =
        """["interpolate", ["linear"], ["get", "value"], 0, "#ff0000", 50, "#00ff00", 100, "#0000ff"]""";

    private static SKColor EvaluateRamp(string expression, object? value) =>
        ExpressionEvaluator.EvaluateColor(MapLibreExpressionParser.Parse(expression), Props(("value", value)));

    /// <summary>
    /// Expected colors are the output of MapLibre GL JS's own reference implementation
    /// (<c>@maplibre/maplibre-gl-style-spec</c>, <c>createExpression(expr, 'fill-color',
    /// latest.paint_fill['fill-color'])</c>) evaluated over the same expression and inputs.
    /// They are not derived from this implementation.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(-10, 247, 251, 255)]
    [InlineData(0, 247, 251, 255)]
    [InlineData(25, 187, 200, 218)]
    [InlineData(50, 128, 149, 181)]
    [InlineData(75, 68, 99, 144)]
    [InlineData(100, 8, 48, 107)]
    [InlineData(150, 8, 48, 107)]
    public void EvaluateColor_TwoStopColorRamp_MatchesMapLibreOutput(double value, byte r, byte g, byte b)
    {
        var result = EvaluateRamp(ChoroplethRamp, value);

        result.Should().Be(new SKColor(r, g, b, 255));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(-5, 255, 0, 0)]
    [InlineData(0, 255, 0, 0)]
    [InlineData(25, 128, 128, 0)]
    [InlineData(50, 0, 255, 0)]
    [InlineData(75, 0, 128, 128)]
    [InlineData(100, 0, 0, 255)]
    [InlineData(120, 0, 0, 255)]
    public void EvaluateColor_MultiStopColorRamp_MatchesMapLibreOutput(double value, byte r, byte g, byte b)
    {
        var result = EvaluateRamp(MultiStopRamp, value);

        result.Should().Be(new SKColor(r, g, b, 255));
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(0, 255, 0, 0, 255)]
    [InlineData(25, 191, 0, 64, 191)]
    [InlineData(50, 128, 0, 128, 128)]
    [InlineData(100, 0, 0, 255, 0)]
    public void EvaluateColor_RgbaStopsWithAlpha_InterpolatesAlphaLikeMapLibre(
        double value, byte r, byte g, byte b, byte a)
    {
        var expression =
            """["interpolate", ["linear"], ["get", "value"], 0, "rgba(255,0,0,1)", 100, "rgba(0,0,255,0)"]""";

        var result = EvaluateRamp(expression, value);

        result.Should().Be(new SKColor(r, g, b, a));
    }

    /// <summary>
    /// A fully transparent stop keeps its RGB channels rather than collapsing to zero:
    /// MapLibre stores colors premultiplied but preserves the original channels for a
    /// zero-alpha color, so the midpoint here is purple, not blue. Straight (non-premultiplied)
    /// interpolation over <see cref="SKColor"/> reproduces that. MapLibre emits rgba(128,0,128,0.5).
    /// </summary>
    [UnitTest]
    public void EvaluateColor_ZeroAlphaStop_PreservesRgbChannelsLikeMapLibre()
    {
        var expression =
            """["interpolate", ["linear"], ["get", "value"], 0, "rgba(255,0,0,0)", 100, "rgba(0,0,255,1)"]""";

        var result = EvaluateRamp(expression, 50.0);

        result.Should().Be(new SKColor(128, 0, 128, 128));
    }

    /// <summary>
    /// The decisive check that interpolation happens in gamma-encoded sRGB rather than
    /// linear-light: MapLibre's midpoint of black and white is 128, where a linear-light
    /// blend would produce roughly 188.
    /// </summary>
    [UnitTest]
    public void EvaluateColor_BlackToWhite_InterpolatesInGammaEncodedSrgbNotLinearLight()
    {
        var expression =
            """["interpolate", ["linear"], ["get", "value"], 0, "#000000", 100, "#ffffff"]""";

        var result = EvaluateRamp(expression, 50.0);

        result.Should().Be(new SKColor(128, 128, 128, 255));
    }

    [UnitTest]
    public void EvaluateColor_NamedColorStops_InterpolatesLikeMapLibre()
    {
        var expression = """["interpolate", ["linear"], ["get", "value"], 0, "red", 100, "blue"]""";

        var result = EvaluateRamp(expression, 50.0);

        result.Should().Be(new SKColor(128, 0, 128, 255));
    }

    [UnitTest]
    public void EvaluateColor_ColorRamp_DoesNotReturnBlack()
    {
        var result = EvaluateRamp(ChoroplethRamp, 50.0);

        result.Should().NotBe(SKColors.Black);
    }

    /// <summary>
    /// Numeric interpolation must be a pure widening: these expectations are MapLibre's
    /// output for the same expression and are also the values the pre-fix evaluator produced.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(-10, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(25, 2.5)]
    [InlineData(50, 5.0)]
    [InlineData(100, 10.0)]
    [InlineData(150, 10.0)]
    public void Evaluate_NumericInterpolate_IsUnchangedByColorSupport(double value, double expected)
    {
        var expr = MapLibreExpressionParser.Parse(
            """["interpolate", ["linear"], ["get", "value"], 0, 0, 100, 10]""");

        var result = ExpressionEvaluator.Evaluate(expr, Props(("value", value)));

        result.Should().BeOfType<double>().Which.Should().BeApproximately(expected, 1e-9);
    }

    [UnitTest]
    public void Evaluate_NumericInterpolate_NumericStringStopsStayNumeric()
    {
        var expr = MapLibreExpressionParser.Parse(
            """["interpolate", ["linear"], ["get", "value"], 0, "0", 100, "10"]""");

        var result = ExpressionEvaluator.Evaluate(expr, Props(("value", 50.0)));

        result.Should().BeOfType<double>().Which.Should().BeApproximately(5.0, 1e-9);
    }

    /// <summary>
    /// MapLibre rejects mixed-type stops when the style is validated
    /// ("Expected color but found number instead."); this evaluator parses styles lazily at
    /// render time, so the equivalent failure has to surface here rather than silently
    /// coercing both stops to 0 and rendering black (honua-server#2867).
    /// </summary>
    [UnitTest]
    public void Evaluate_InterpolateWithMixedColorAndNumberStops_Throws()
    {
        var expr = MapLibreExpressionParser.Parse(
            """["interpolate", ["linear"], ["get", "value"], 0, "#ff0000", 100, 42]""");

        var act = () => ExpressionEvaluator.Evaluate(expr, Props(("value", 50.0)));

        act.Should().Throw<StyleExpressionEvaluationException>()
            .WithMessage("*color*number*");
    }

    /// <summary>
    /// MapLibre rejects this at validation time with
    /// "Could not parse color from value 'not-a-color'".
    /// </summary>
    [UnitTest]
    public void Evaluate_InterpolateWithUnparseableColorStop_Throws()
    {
        var expr = MapLibreExpressionParser.Parse(
            """["interpolate", ["linear"], ["get", "value"], 0, "not-a-color", 100, "#08306b"]""");

        var act = () => ExpressionEvaluator.Evaluate(expr, Props(("value", 50.0)));

        act.Should().Throw<StyleExpressionEvaluationException>();
    }

    [UnitTest]
    public void Evaluate_InterpolateWithNullStopOutput_Throws()
    {
        var expr = MapLibreExpressionParser.Parse(
            """["interpolate", ["linear"], ["get", "value"], 0, ["get", "missing"], 100, 10]""");

        var act = () => ExpressionEvaluator.Evaluate(expr, Props(("value", 50.0)));

        act.Should().Throw<StyleExpressionEvaluationException>();
    }

    /// <summary>
    /// Landing exactly on a stop must yield that stop's own color rather than a rounded
    /// neighbour, at the first stop, an interior stop, and the last stop.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(0, 255, 0, 0)]
    [InlineData(50, 0, 255, 0)]
    [InlineData(100, 0, 0, 255)]
    public void EvaluateColor_ExactStopBoundary_ReturnsStopColorExactly(double value, byte r, byte g, byte b)
    {
        var result = EvaluateRamp(MultiStopRamp, value);

        result.Should().Be(new SKColor(r, g, b, 255));
    }

    private static double EvaluateNumeric(string expression, double value) =>
        ((double)ExpressionEvaluator.Evaluate(
            MapLibreExpressionParser.Parse(expression), Props(("value", value)))!);

    /// <summary>
    /// Linear numeric interpolation must remain a pure widening: expectations are MapLibre
    /// GL JS's own output for <c>["interpolate", ["linear"], ...]</c> and are byte-identical
    /// to the pre-fix (type-ignoring) evaluator, proving the type-operand change did not move
    /// the linear path. Captured via <c>createExpression(expr, 'line-width',
    /// latest.paint_line['line-width'])</c>.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(-10, 0.0)]
    [InlineData(0, 0.0)]
    [InlineData(10, 1.0)]
    [InlineData(25, 2.5)]
    [InlineData(50, 5.0)]
    [InlineData(75, 7.5)]
    [InlineData(100, 10.0)]
    [InlineData(150, 10.0)]
    public void Evaluate_LinearNumericInterpolate_MatchesMapLibreAndIsBitUnchanged(double value, double expected)
    {
        var result = EvaluateNumeric(
            """["interpolate", ["linear"], ["get", "value"], 0, 0, 100, 10]""", value);

        result.Should().Be(expected);
    }

    /// <summary>
    /// <c>["exponential", 1]</c> is MapLibre's own degenerate case: it must equal
    /// <c>["linear"]</c> exactly (its factor collapses to <c>progress / difference</c>).
    /// Expectations are MapLibre's output and match the linear ramp above stop-for-stop.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(0, 0.0)]
    [InlineData(10, 1.0)]
    [InlineData(25, 2.5)]
    [InlineData(50, 5.0)]
    [InlineData(75, 7.5)]
    [InlineData(100, 10.0)]
    public void Evaluate_ExponentialBaseOne_EqualsLinear(double value, double expected)
    {
        var exponential = EvaluateNumeric(
            """["interpolate", ["exponential", 1], ["get", "value"], 0, 0, 100, 10]""", value);
        var linear = EvaluateNumeric(
            """["interpolate", ["linear"], ["get", "value"], 0, 0, 100, 10]""", value);

        exponential.Should().Be(linear);
        exponential.Should().Be(expected);
    }

    /// <summary>
    /// Exponential (base 2 and base 0.5) numeric ramps over 0..100 -> 0..10. Expectations are
    /// MapLibre GL JS's own output — the discrepancy this ticket exists for (the type-ignoring
    /// evaluator returned the linear value here instead). Tolerance absorbs the single-precision
    /// factor cast; the values differ from linear by orders of magnitude, so the curve is proven.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(2.0, 90, 0.009765625)]
    [InlineData(2.0, 100, 10.0)]
    [InlineData(0.5, 10, 9.990234375)]
    [InlineData(0.5, 50, 9.999999999999991)]
    public void Evaluate_ExponentialNumericInterpolate_MatchesMapLibreOutput(
        double @base, double value, double expected)
    {
        var expression =
            $$"""["interpolate", ["exponential", {{@base.ToString(CultureInfo.InvariantCulture)}}], ["get", "value"], 0, 0, 100, 10]""";

        var result = EvaluateNumeric(expression, value);

        result.Should().BeApproximately(expected, 1e-5);
    }

    /// <summary>
    /// The bug's canonical shape: an exponential (base 2) ramp evaluated between two stops
    /// returns a value that a linear ramp never would. MapLibre GL JS yields 2.0 here (stops
    /// 10 -> 0, 14 -> 10 at input 12); the linear reading is 5.0.
    /// </summary>
    [UnitTest]
    public void Evaluate_ExponentialRamp_DiffersFromLinearReading()
    {
        var exponential = EvaluateNumeric(
            """["interpolate", ["exponential", 2], ["get", "value"], 10, 0, 14, 10]""", 12);
        var linear = EvaluateNumeric(
            """["interpolate", ["linear"], ["get", "value"], 10, 0, 14, 10]""", 12);

        exponential.Should().BeApproximately(2.0, 1e-5);
        linear.Should().Be(5.0);
    }

    /// <summary>
    /// Multi-stop exponential selects the right interval before applying the curve.
    /// Expectations are MapLibre GL JS's output for stops 0 -> 0, 50 -> 5, 100 -> 100.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(50, 5.0)]
    [InlineData(90, 5.092773437499916)]
    [InlineData(100, 100.0)]
    public void Evaluate_MultiStopExponential_SelectsIntervalThenCurves(double value, double expected)
    {
        var result = EvaluateNumeric(
            """["interpolate", ["exponential", 2], ["get", "value"], 0, 0, 50, 5, 100, 100]""", value);

        result.Should().BeApproximately(expected, 1e-5);
    }

    /// <summary>
    /// Cubic-bezier numeric easing. Expectations are MapLibre GL JS's unit-bezier solver output
    /// for control points (0.42, 0, 0.58, 1) over stops 0 -> 0, 100 -> 10.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(10, 0.1972244726385564)]
    [InlineData(25, 1.2916190056878776)]
    [InlineData(50, 5.0)]
    [InlineData(75, 8.708380994312122)]
    [InlineData(90, 9.802775527361444)]
    public void Evaluate_CubicBezierNumericInterpolate_MatchesMapLibreUnitBezier(double value, double expected)
    {
        var result = EvaluateNumeric(
            """["interpolate", ["cubic-bezier", 0.42, 0, 0.58, 1], ["get", "value"], 0, 0, 100, 10]""", value);

        result.Should().BeApproximately(expected, 1e-5);
    }

    /// <summary>
    /// The type operand applies on the color path too (post-#2867/#2870). Expectations are
    /// MapLibre GL JS's <c>fill-color</c> output for an exponential base-2 ramp between
    /// #f7fbff and #08306b: base 2 keeps the color near the low stop until close to 100.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(50, 247, 251, 255)]
    [InlineData(90, 247, 251, 255)]
    [InlineData(100, 8, 48, 107)]
    public void EvaluateColor_ExponentialColorRamp_MatchesMapLibreOutput(double value, byte r, byte g, byte b)
    {
        var expression =
            """["interpolate", ["exponential", 2], ["get", "value"], 0, "#f7fbff", 100, "#08306b"]""";

        var result = EvaluateRamp(expression, value);

        result.Should().Be(new SKColor(r, g, b, 255));
    }

    /// <summary>
    /// Cubic-bezier easing on the color path. Expectations are MapLibre GL JS's
    /// <c>fill-color</c> output for control points (0.42, 0, 0.58, 1) between #f7fbff and
    /// #08306b.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(25, 216, 225, 236)]
    [InlineData(50, 128, 149, 181)]
    [InlineData(75, 39, 74, 126)]
    public void EvaluateColor_CubicBezierColorRamp_MatchesMapLibreOutput(double value, byte r, byte g, byte b)
    {
        var expression =
            """["interpolate", ["cubic-bezier", 0.42, 0, 0.58, 1], ["get", "value"], 0, "#f7fbff", 100, "#08306b"]""";

        var result = EvaluateRamp(expression, value);

        result.Should().Be(new SKColor(r, g, b, 255));
    }

    /// <summary>
    /// <c>["exponential", 1]</c> on the color path is also the degenerate linear case: its
    /// output must equal the linear <c>ChoroplethRamp</c> bytes MapLibre emits (187,200,218 at
    /// 25; 128,149,181 at 50; 68,99,144 at 75), proving base-1 does not disturb existing color
    /// ramps.
    /// </summary>
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData(25, 187, 200, 218)]
    [InlineData(50, 128, 149, 181)]
    [InlineData(75, 68, 99, 144)]
    public void EvaluateColor_ExponentialBaseOneColorRamp_EqualsLinear(double value, byte r, byte g, byte b)
    {
        var exponential = EvaluateRamp(
            """["interpolate", ["exponential", 1], ["get", "value"], 0, "#f7fbff", 100, "#08306b"]""", value);
        var linear = EvaluateRamp(ChoroplethRamp, value);

        exponential.Should().Be(linear);
        exponential.Should().Be(new SKColor(r, g, b, 255));
    }

    [UnitTest]
    public void TryParseColor_UnrecognizedValue_ReturnsFalseInsteadOfBlack()
    {
        ExpressionEvaluator.TryParseColor("not-a-color", out _).Should().BeFalse();
        ExpressionEvaluator.TryParseColor(null, out _).Should().BeFalse();
        ExpressionEvaluator.TryParseColor("#08306b", out var parsed).Should().BeTrue();
        parsed.Should().Be(new SKColor(8, 48, 107, 255));
    }

    /// <summary>
    /// <see cref="ExpressionEvaluator.ParseColor"/> keeps its existing fallbacks — transparent
    /// for absent values, black for unrecognized ones — now that it delegates to the
    /// failure-reporting parser.
    /// </summary>
    [UnitTest]
    public void ParseColor_FallbackBehaviour_IsUnchanged()
    {
        ExpressionEvaluator.ParseColor(null).Should().Be(SKColors.Transparent);
        ExpressionEvaluator.ParseColor("").Should().Be(SKColors.Transparent);
        ExpressionEvaluator.ParseColor("not-a-color").Should().Be(SKColors.Black);
        ExpressionEvaluator.ParseColor("#ff0000").Should().Be(SKColors.Red);
        ExpressionEvaluator.ParseColor("rgb(1,2,3)").Should().Be(new SKColor(1, 2, 3, 255));
        ExpressionEvaluator.ParseColor("rgb(bad)").Should().Be(SKColors.Black);
    }
}
