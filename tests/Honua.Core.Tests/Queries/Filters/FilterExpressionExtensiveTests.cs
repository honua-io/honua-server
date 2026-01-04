// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

/// <summary>
/// Comprehensive tests for filter expression AST construction and structure.
/// </summary>
public class FilterExpressionExtensiveTests
{
    [UnitTest]
    public void PropertyReference_WithName_ShouldPreserveName()
    {
        var reference = new PropertyReference("status");

        reference.PropertyName.Should().Be("status");
    }

    [UnitTest]
    public void Literal_WithTextValue_ShouldPreserveValue()
    {
        var literal = new Literal("active", LiteralType.Text);

        literal.Value.Should().Be("active");
        literal.Type.Should().Be(LiteralType.Text);
    }

    [UnitTest]
    public void Literal_WithNumberValue_ShouldPreserveValue()
    {
        var literal = new Literal(123.45m, LiteralType.Number);

        literal.Value.Should().Be(123.45m);
        literal.Type.Should().Be(LiteralType.Number);
    }

    [UnitTest]
    public void BinaryExpression_WithComparisonOperator_ShouldHoldOperands()
    {
        var left = new PropertyReference("value");
        var right = new Literal(500, LiteralType.Number);

        var expression = new BinaryExpression(left, BinaryOperator.GreaterThanOrEqual, right);

        expression.Left.Should().Be(left);
        expression.Right.Should().Be(right);
        expression.Operator.Should().Be(BinaryOperator.GreaterThanOrEqual);
    }

    [UnitTest]
    public void BinaryExpression_WithLogicalOperator_ShouldCombineExpressions()
    {
        var left = new BinaryExpression(
            new PropertyReference("status"),
            BinaryOperator.Equal,
            new Literal("active", LiteralType.Text));

        var right = new BinaryExpression(
            new PropertyReference("priority"),
            BinaryOperator.Equal,
            new Literal("high", LiteralType.Text));

        var combined = new BinaryExpression(left, BinaryOperator.And, right);

        combined.Left.Should().Be(left);
        combined.Right.Should().Be(right);
        combined.Operator.Should().Be(BinaryOperator.And);
    }

    [UnitTest]
    public void UnaryExpression_IsNull_ShouldWrapOperand()
    {
        var operand = new PropertyReference("description");

        var expression = new UnaryExpression(UnaryOperator.IsNull, operand);

        expression.Operator.Should().Be(UnaryOperator.IsNull);
        expression.Operand.Should().Be(operand);
    }

    [UnitTest]
    public void UnaryExpression_IsNotNull_ShouldWrapOperand()
    {
        var operand = new PropertyReference("description");

        var expression = new UnaryExpression(UnaryOperator.IsNotNull, operand);

        expression.Operator.Should().Be(UnaryOperator.IsNotNull);
        expression.Operand.Should().Be(operand);
    }

    [UnitTest]
    public void InOperator_WithValueList_ShouldHoldValues()
    {
        var property = new PropertyReference("category");
        var values = new ValueList(new FilterExpression[]
        {
            new Literal("retail", LiteralType.Text),
            new Literal("commercial", LiteralType.Text)
        });

        var expression = new BinaryExpression(property, BinaryOperator.In, values);

        expression.Operator.Should().Be(BinaryOperator.In);
        expression.Left.Should().Be(property);
        expression.Right.Should().Be(values);
    }

    [UnitTest]
    public void SpatialPredicate_Intersects_ShouldHoldOperands()
    {
        var property = new PropertyReference("geometry");
        var geometry = new GeometryLiteral(new byte[] { 0x01, 0x02 }, 4326, "WKB");

        var predicate = new SpatialPredicate(SpatialOperator.Intersects, property, geometry);

        predicate.Operator.Should().Be(SpatialOperator.Intersects);
        predicate.Left.Should().Be(property);
        predicate.Right.Should().Be(geometry);
    }

    [UnitTest]
    public void TemporalPredicate_After_ShouldHoldOperands()
    {
        var property = new PropertyReference("created_at");
        var instant = new Literal(DateTime.UnixEpoch, LiteralType.DateTime);

        var predicate = new TemporalPredicate(TemporalOperator.After, property, instant);

        predicate.Operator.Should().Be(TemporalOperator.After);
        predicate.Left.Should().Be(property);
        predicate.Right.Should().Be(instant);
    }

    [UnitTest]
    public void DeepNesting_ShouldNotCauseStackOverflow()
    {
        FilterExpression expression = new Literal(true, LiteralType.Boolean);

        for (var i = 0; i < 500; i++)
        {
            var next = new BinaryExpression(
                new PropertyReference($"prop_{i}"),
                BinaryOperator.Equal,
                new Literal(i, LiteralType.Number));

            expression = new BinaryExpression(expression, BinaryOperator.And, next);
        }

        expression.Should().BeOfType<BinaryExpression>();
    }
}
