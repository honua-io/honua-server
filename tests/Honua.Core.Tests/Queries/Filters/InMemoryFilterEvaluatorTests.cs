// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class InMemoryFilterEvaluatorTests
{
    [UnitTest]
    public void TryValidateStreamingExpression_FunctionCall_ReturnsFalse()
    {
        var expression = new BinaryExpression(
            new FunctionCall("UPPER", [new PropertyReference("name")]),
            BinaryOperator.Equal,
            new Literal("ALICE", LiteralType.Text));

        var result = InMemoryFilterEvaluator.TryValidateStreamingExpression(expression, out var error);

        result.Should().BeFalse();
        error.Should().Contain("function calls");
    }

    [UnitTest]
    public void Evaluate_UnsupportedExpression_ReturnsFalse()
    {
        var expression = new BinaryExpression(
            new FunctionCall("UPPER", [new PropertyReference("name")]),
            BinaryOperator.Equal,
            new Literal("ALICE", LiteralType.Text));

        var properties = CreateProperties("""{"name":"alice"}""");

        InMemoryFilterEvaluator.Evaluate(expression, properties).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NotInWithMissingProperty_ReturnsFalse()
    {
        var expression = new BinaryExpression(
            new PropertyReference("status"),
            BinaryOperator.NotIn,
            new ValueList([new Literal("active", LiteralType.Text)]));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_StringEquality_IsCaseSensitive()
    {
        var expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Equal,
            new Literal("ALPHA", LiteralType.Text));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("""{"name":"alpha"}""")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_Like_IsCaseSensitive()
    {
        var expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Like,
            new Literal("ALP%", LiteralType.Text));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("""{"name":"alpha"}""")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_BigIntegerEquality_PreservesPrecision()
    {
        const long objectId = 9_007_199_254_740_993L;
        var expression = new BinaryExpression(
            new PropertyReference("objectid"),
            BinaryOperator.Equal,
            new Literal(objectId, LiteralType.Number));

        var properties = CreateProperties($$"""{"objectid":{{objectId}}}""");

        InMemoryFilterEvaluator.Evaluate(expression, properties).Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_BigIntegerMismatch_DoesNotCollapseToDoublePrecision()
    {
        const long propertyValue = 9_007_199_254_740_993L;
        const long literalValue = 9_007_199_254_740_992L;
        var expression = new BinaryExpression(
            new PropertyReference("objectid"),
            BinaryOperator.Equal,
            new Literal(literalValue, LiteralType.Number));

        var properties = CreateProperties($$"""{"objectid":{{propertyValue}}}""");

        InMemoryFilterEvaluator.Evaluate(expression, properties).Should().BeFalse();
    }

    private static Dictionary<string, JsonElement> CreateProperties(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
    }
}
