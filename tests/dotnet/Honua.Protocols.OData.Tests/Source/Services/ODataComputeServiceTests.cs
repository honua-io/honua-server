// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using Honua.Protocols.OData.Services;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

/// <summary>
/// Unit tests for the shared OData <c>$compute</c> grammar (#1620). The compute service is the
/// single source of truth for both the <c>$compute</c> query option and the
/// <c>$apply=compute(...)</c> transform, and must support the canonical <c>floor()</c> function
/// (plus <c>ceiling()</c>, <c>round()</c>, and the <c>mod</c> operator) so histogram binning is
/// first-class instead of requiring the <c>x sub (x mod w)</c> workaround.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[Trait("Feature", "Compute")]
public sealed class ODataComputeServiceTests
{
    private static double? EvaluateSingle(string expression, IReadOnlyDictionary<string, object?> values)
    {
        Assert.True(ODataComputeService.TryParse(expression, out var parsed, out var error), error);
        Assert.Single(parsed);
        return ODataComputeService.Evaluate(values, parsed[0]);
    }

    [Theory]
    [InlineData("x add 1 as r", 5d, 6d)]
    [InlineData("x sub 1 as r", 5d, 4d)]
    [InlineData("x mul 3 as r", 5d, 15d)]
    [InlineData("x div 2 as r", 5d, 2.5d)]
    [InlineData("x mod 3 as r", 5d, 2d)]
    public void Evaluate_Arithmetic_ReturnsExpected(string expression, double input, double expected)
    {
        var result = EvaluateSingle(expression, new Dictionary<string, object?> { ["x"] = input });
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Evaluate_DivideByZero_ReturnsNull()
    {
        var result = EvaluateSingle("x div 0 as r", new Dictionary<string, object?> { ["x"] = 5d });
        Assert.Null(result);
    }

    [Fact]
    public void Evaluate_ModuloByZero_ReturnsNull()
    {
        var result = EvaluateSingle("x mod 0 as r", new Dictionary<string, object?> { ["x"] = 5d });
        Assert.Null(result);
    }

    [Theory]
    [InlineData("floor(x) as r", 5.9d, 5d)]
    [InlineData("ceiling(x) as r", 5.1d, 6d)]
    [InlineData("round(x) as r", 5.4d, 5d)]
    [InlineData("round(x) as r", 5.6d, 6d)]
    public void Evaluate_SingleOperandFunction_ReturnsExpected(string expression, double input, double expected)
    {
        var result = EvaluateSingle(expression, new Dictionary<string, object?> { ["x"] = input });
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Evaluate_FloorBinning_BucketsValue()
    {
        // The canonical histogram binning expression: floor(value div width).
        var result = EvaluateSingle(
            "floor(population div 1000000) as bin",
            new Dictionary<string, object?> { ["population"] = 3979576L });

        Assert.Equal(3d, result);
    }

    [Fact]
    public void Evaluate_FloorWithArithmetic_AppliesOperatorThenFunction()
    {
        var result = EvaluateSingle(
            "floor(x mul 1.5) as r",
            new Dictionary<string, object?> { ["x"] = 3d });

        // 3 * 1.5 = 4.5 -> floor = 4
        Assert.Equal(4d, result);
    }

    [Fact]
    public void Evaluate_BareField_CopiesValue()
    {
        var result = EvaluateSingle("x as r", new Dictionary<string, object?> { ["x"] = 7d });
        Assert.Equal(7d, result);
    }

    [Fact]
    public void Evaluate_MissingOperand_ReturnsNull()
    {
        var result = EvaluateSingle("missing add 1 as r", new Dictionary<string, object?> { ["x"] = 5d });
        Assert.Null(result);
    }

    [Fact]
    public void TryParse_MultipleSegments_ParsesEach()
    {
        Assert.True(
            ODataComputeService.TryParse("a mul 2 as d, floor(b div 10) as bin", out var parsed, out var error),
            error);

        Assert.Equal(2, parsed.Length);
        Assert.Equal("d", parsed[0].Alias);
        Assert.Equal("bin", parsed[1].Alias);
        Assert.Equal(ODataComputeFunction.Floor, parsed[1].Function);
    }

    [Fact]
    public void TryParse_DuplicateAlias_Fails()
    {
        Assert.False(
            ODataComputeService.TryParse("x add 1 as r, y add 1 as r", out _, out var error));
        Assert.Contains("Duplicate", error, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x foo 1 as r")]
    [InlineData("sqrt(x) as r")]
    [InlineData("x add 1")]
    [InlineData("floor(x as r")]
    public void TryParse_InvalidExpression_Fails(string expression)
    {
        Assert.False(ODataComputeService.TryParse(expression, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ApplyCompute_WritesAliasIntoPayload()
    {
        Assert.True(
            ODataComputeService.TryParse("floor(population div 1000000) as PopBin", out var parsed, out _));

        var payload = new Dictionary<string, object?> { ["population"] = 1423851L };
        ODataComputeService.ApplyCompute(payload, parsed);

        Assert.Equal(1d, payload["PopBin"]);
    }
}
