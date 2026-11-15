// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class InMemoryFilterEvaluatorTests
{
    [UnitTest]
    public void ExceedsMaxDepth_LeftDeepChainFarBeyondLimit_ReturnsTrue()
    {
        // The measurement stops descending once the streaming limit is passed, so its
        // cost does not grow with the depth of the chain it is handed.
        FilterExpression expression = new Literal(true, LiteralType.Boolean);
        for (var i = 0; i < 5_000; i++)
        {
            expression = new BinaryExpression(expression, BinaryOperator.And, new Literal(true, LiteralType.Boolean));
        }

        InMemoryFilterEvaluator.ExceedsMaxDepth(expression).Should().BeTrue();
    }

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
    public void TryValidateStreamingExpression_NullBooleanShapes_ReturnTrue()
    {
        FilterExpression[] expressions =
        [
            new Literal(null, LiteralType.Null),
            new UnaryExpression(UnaryOperator.Not, new Literal(null, LiteralType.Null)),
            new BinaryExpression(
                new Literal(null, LiteralType.Null),
                BinaryOperator.Or,
                new Literal(true, LiteralType.Boolean))
        ];

        foreach (var expression in expressions)
        {
            InMemoryFilterEvaluator.TryValidateStreamingExpression(expression, out var error)
                .Should().BeTrue(error);
        }
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
    public void Evaluate_Like_MatchesABoundedWildcard()
    {
        var expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Like,
            new Literal("%secret%", LiteralType.Text));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("""{"name":"top-secret"}""")).Should().BeTrue();
    }

    [UnitTest]
    public void Evaluate_Like_RefusesTooManyWildcardsWithoutEchoingThePattern()
    {
        const string marker = "saved-like-marker";
        var expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Like,
            new Literal(marker + new string('%', 33), LiteralType.Text));

        var error = Assert.Throws<UnevaluableLikePatternException>(() =>
            InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("""{"name":"eeee"}""")));

        error.ToString().Should().NotContain(marker);
    }

    [UnitTest]
    public void Evaluate_NotLike_RefusesTooManyWildcardsInsteadOfInverting()
    {
        var expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.NotLike,
            new Literal(new string('_', 33), LiteralType.Text));

        var act = () => InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("""{"name":"eeee"}"""));

        act.Should().Throw<UnevaluableLikePatternException>();
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

    [UnitTest]
    public void Evaluate_NotUnknown_ReturnsFalse()
    {
        var expression = new UnaryExpression(
            UnaryOperator.Not,
            new Literal(null, LiteralType.Null));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NotUnknownAndTrue_ReturnsFalse()
    {
        var expression = new UnaryExpression(
            UnaryOperator.Not,
            new BinaryExpression(
                new Literal(null, LiteralType.Null),
                BinaryOperator.And,
                new Literal(true, LiteralType.Boolean)));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NotUnknownOrFalse_ReturnsFalse()
    {
        var expression = new UnaryExpression(
            UnaryOperator.Not,
            new BinaryExpression(
                new Literal(null, LiteralType.Null),
                BinaryOperator.Or,
                new Literal(false, LiteralType.Boolean)));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_NotInWithNullListMemberAndNoMatch_ReturnsFalse()
    {
        var expression = new BinaryExpression(
            new PropertyReference("status"),
            BinaryOperator.NotIn,
            new ValueList(
                [new Literal("active", LiteralType.Text), new Literal(null, LiteralType.Null)]));

        InMemoryFilterEvaluator.Evaluate(
            expression,
            CreateProperties("""{"status":"inactive"}""")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_UnknownAndFalse_ReturnsFalse()
    {
        var expression = new BinaryExpression(
            new Literal(null, LiteralType.Null),
            BinaryOperator.And,
            new Literal(false, LiteralType.Boolean));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeFalse();
    }

    [UnitTest]
    public void Evaluate_UnknownOrTrue_ReturnsTrue()
    {
        var expression = new BinaryExpression(
            new Literal(null, LiteralType.Null),
            BinaryOperator.Or,
            new Literal(true, LiteralType.Boolean));

        InMemoryFilterEvaluator.Evaluate(expression, CreateProperties("{}")).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(256, true)]
    [InlineData(257, false)]
    public void Evaluate_Like_EnforcesPatternLengthBoundary(int length, bool accepted)
    {
        var pattern = new string('a', length);
        var expression = new BinaryExpression(new PropertyReference("name"), BinaryOperator.Like,
            new Literal(pattern, LiteralType.Text));
        var properties = CreateProperties($"{{\"name\":\"{pattern}\"}}");

        if (accepted)
        {
            InMemoryFilterEvaluator.Evaluate(expression, properties).Should().BeTrue();
        }
        else
        {
            Action act = () => InMemoryFilterEvaluator.Evaluate(expression, properties);
            act.Should().Throw<UnevaluableLikePatternException>();
        }
    }

    [UnitTheory]
    [InlineData(32, true)]
    [InlineData(33, false)]
    public void Evaluate_Like_EnforcesWildcardBoundary(int count, bool accepted)
    {
        var expression = new BinaryExpression(new PropertyReference("name"), BinaryOperator.Like,
            new Literal(new string('_', count), LiteralType.Text));
        var properties = CreateProperties($"{{\"name\":\"{new string('a', count)}\"}}");

        if (accepted)
        {
            InMemoryFilterEvaluator.Evaluate(expression, properties).Should().BeTrue();
        }
        else
        {
            Action act = () => InMemoryFilterEvaluator.Evaluate(expression, properties);
            act.Should().Throw<UnevaluableLikePatternException>();
        }
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
