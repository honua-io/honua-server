// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.MapServer.Rendering;
using Honua.TestKit.Attributes;
using SkiaSharp;

namespace Honua.Server.Tests.Features.MapServer.Rendering;

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
        var expr = JsonDocument.Parse("""["get", "name"]""").RootElement;
        var props = Props(("name", "Test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("Test");
    }

    [UnitTest]
    public void Evaluate_GetExpression_MissingProperty_ReturnsNull()
    {
        var expr = JsonDocument.Parse("""["get", "missing"]""").RootElement;

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().BeNull();
    }

    [UnitTest]
    public void Evaluate_HasExpression_ReturnsTrueWhenPresent()
    {
        var expr = JsonDocument.Parse("""["has", "name"]""").RootElement;
        var props = Props(("name", "Test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_HasExpression_ReturnsFalseWhenMissing()
    {
        var expr = JsonDocument.Parse("""["has", "missing"]""").RootElement;

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_NotExpression_InvertsTruthiness()
    {
        var expr = JsonDocument.Parse("""["!", ["has", "name"]]""").RootElement;

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_EqualityComparison_ReturnsTrue()
    {
        var expr = JsonDocument.Parse("""["==", ["get", "type"], "road"]""").RootElement;
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_EqualityComparison_ReturnsFalse()
    {
        var expr = JsonDocument.Parse("""["==", ["get", "type"], "road"]""").RootElement;
        var props = Props(("type", "building"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_LessThan_ReturnsCorrectResult()
    {
        var expr = JsonDocument.Parse("""["<", ["get", "population"], 1000]""").RootElement;
        var props = Props(("population", 500));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_GreaterThan_ReturnsCorrectResult()
    {
        var expr = JsonDocument.Parse(""" [">", ["get", "population"], 1000]""").RootElement;
        var props = Props(("population", 500));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_AllExpression_ReturnsTrueWhenAllTrue()
    {
        var expr = JsonDocument.Parse("""["all", ["has", "name"], ["has", "type"]]""").RootElement;
        var props = Props(("name", "test"), ("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_AllExpression_ReturnsFalseWhenAnyFalse()
    {
        var expr = JsonDocument.Parse("""["all", ["has", "name"], ["has", "missing"]]""").RootElement;
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(false);
    }

    [UnitTest]
    public void Evaluate_AnyExpression_ReturnsTrueWhenAnyTrue()
    {
        var expr = JsonDocument.Parse("""["any", ["has", "missing"], ["has", "name"]]""").RootElement;
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be(true);
    }

    [UnitTest]
    public void Evaluate_MatchExpression_ReturnsMatchedValue()
    {
        var expr = JsonDocument.Parse("""["match", ["get", "type"], "road", "blue", "building", "red", "gray"]""").RootElement;
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("blue");
    }

    [UnitTest]
    public void Evaluate_MatchExpression_ReturnsFallback()
    {
        var expr = JsonDocument.Parse("""["match", ["get", "type"], "road", "blue", "building", "red", "gray"]""").RootElement;
        var props = Props(("type", "park"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("gray");
    }

    [UnitTest]
    public void Evaluate_CaseExpression_ReturnsFirstMatchingBranch()
    {
        var expr = JsonDocument.Parse("""["case", ["==", ["get", "type"], "road"], "blue", "gray"]""").RootElement;
        var props = Props(("type", "road"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("blue");
    }

    [UnitTest]
    public void Evaluate_CaseExpression_ReturnsFallback()
    {
        var expr = JsonDocument.Parse("""["case", ["==", ["get", "type"], "road"], "blue", "gray"]""").RootElement;
        var props = Props(("type", "park"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("gray");
    }

    [UnitTest]
    public void Evaluate_ArithmeticAdd_ReturnsSum()
    {
        var expr = JsonDocument.Parse("""["+", 10, 20]""").RootElement;

        var result = ExpressionEvaluator.Evaluate(expr, _emptyProps);

        result.Should().BeOfType<double>().Which.Should().Be(30.0);
    }

    [UnitTest]
    public void Evaluate_CoalesceExpression_ReturnsFirstNonNull()
    {
        var expr = JsonDocument.Parse("""["coalesce", ["get", "missing"], ["get", "name"]]""").RootElement;
        var props = Props(("name", "test"));

        var result = ExpressionEvaluator.Evaluate(expr, props);

        result.Should().Be("test");
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
        var expr = JsonDocument.Parse("5.0").RootElement;

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
}
