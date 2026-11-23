// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.FeatureServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.FeatureServer;

public sealed class FeatureServerCalculateExpressionTests
{
    [UnitTest]
    public void TryInspectCalcExpression_FieldReference_ReportsSourceIdentifier()
    {
        var parsed = FeatureServerEndpoints.TryInspectCalcExpression(
            "secret || name",
            out var sources,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        sources.Should().BeEquivalentTo(["secret", "name"]);
    }

    [UnitTest]
    public void TryInspectCalcExpression_FunctionAndKeywords_SkipsNonFields()
    {
        var parsed = FeatureServerEndpoints.TryInspectCalcExpression(
            "UPPER(secret) || NULL",
            out var sources,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        sources.Should().Equal("secret");
    }

    [UnitTest]
    public void TryEvaluateCalcExpression_MixedCaseIdentifier_ReadsAttribute()
    {
        var attributes = new Dictionary<string, object?> { ["name"] = "Ada" };

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            "NAME || '_ok'",
            attributes,
            out var value,
            out var error);

        parsed.Should().BeTrue();
        error.Should().BeNull();
        value.Should().Be("Ada_ok");
    }

    [UnitTheory]
    [InlineData("UPPER(name)")]
    [InlineData("LOWER(name)")]
    [InlineData("TRIM(name)")]
    [InlineData("LENGTH(name)")]
    [InlineData("pop + 1")]
    [InlineData("pop - 1")]
    [InlineData("pop * 2")]
    [InlineData("pop / 2")]
    [InlineData("10 / pop")]
    [InlineData("pop % 2")]
    [InlineData("-pop")]
    public void TryEvaluateCalcExpression_NullOperand_YieldsNull(string expression)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = null, ["pop"] = null };

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression,
            attributes,
            out var value,
            out var error);

        parsed.Should().BeTrue(error);
        error.Should().BeNull();
        value.Should().BeNull();
    }

    [UnitTest]
    public void TryEvaluateCalcExpression_NullOperandWithCoalesce_UsesFallback()
    {
        var attributes = new Dictionary<string, object?> { ["pop"] = null };

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            "COALESCE(pop, 0) + 1",
            attributes,
            out var value,
            out var error);

        parsed.Should().BeTrue(error);
        value.Should().Be(1m);
    }

    [UnitTheory]
    [InlineData("name * 2")]
    [InlineData("-name")]
    public void TryEvaluateCalcExpression_NonNumericOperand_StillFails(string expression)
    {
        var attributes = new Dictionary<string, object?> { ["name"] = "abc" };

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression,
            attributes,
            out _,
            out var error);

        parsed.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [UnitTheory]
    [InlineData("79228162514264337593543950335 + 1")]
    [InlineData("-79228162514264337593543950335 - 1")]
    [InlineData("79228162514264337593543950335 * 2")]
    [InlineData("big * big")]
    [InlineData("big / 0.5")]
    public void TryEvaluateCalcExpression_NumericOverflow_ReturnsExpressionError(string expression)
    {
        var attributes = new Dictionary<string, object?> { ["big"] = 79228162514264337593543950335m };

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression,
            attributes,
            out var value,
            out var error);

        parsed.Should().BeFalse();
        value.Should().BeNull();
        error.Should().Contain("numeric range");
    }

    [UnitTheory]
    [InlineData(2, 1)]
    [InlineData(3, -1)]
    public void CalcExpression_RepeatedUnaryMinus_FoldsWithoutNesting(int count, int expected)
    {
        var expression = new string('-', count) + "1";

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression, new Dictionary<string, object?>(), out var value, out var error);

        parsed.Should().BeTrue(error);
        value.Should().Be((decimal)expected);
    }

    [UnitTest]
    public void CalcExpression_NestingWithinLimit_StillEvaluates()
    {
        var expression = new string('(', 2) + "1 + 1" + new string(')', 2);

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression, new Dictionary<string, object?>(), out var value, out var error);

        parsed.Should().BeTrue(error);
        value.Should().Be(2m);
    }

    [UnitTest]
    public void CalcExpression_FlatExpression_Evaluates()
    {
        var expression = string.Join(" + ", Enumerable.Repeat("1", 5));

        var parsed = FeatureServerEndpoints.TryEvaluateCalcExpression(
            expression, new Dictionary<string, object?>(), out var value, out var error);

        parsed.Should().BeTrue(error);
        value.Should().Be(5m);
    }
}
