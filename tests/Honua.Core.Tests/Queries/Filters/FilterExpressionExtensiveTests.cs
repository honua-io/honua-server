// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using FsCheck;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;
using Honua.TestKit.PropertyBased;

namespace Honua.Core.Tests.Queries.Filters;

/// <summary>
/// Comprehensive tests for FilterExpression parsing and validation
/// </summary>
public class FilterExpressionExtensiveTests
{
    [UnitTest]
    public Property PropertyFilter_WithValidProperty_ShouldCreate()
    {
        return Prop.ForAll(FilterExpressionGenerators.ArbitraryPropertyName(), propertyName =>
        {
            var filter = FilterExpression.Property(propertyName);
            return filter.Type == FilterExpressionType.Property &&
                   filter.Property == propertyName;
        });
    }

    [UnitTest]
    public Property LiteralFilter_WithValue_ShouldPreserveValue()
    {
        return Prop.ForAll(FilterExpressionGenerators.ArbitraryLiteralValue(), value =>
        {
            var filter = FilterExpression.Literal(value);
            return filter.Type == FilterExpressionType.Literal &&
                   Equals(filter.Value, value);
        });
    }

    [UnitTest]
    public Property ComparisonFilter_ShouldHaveCorrectStructure()
    {
        return Prop.ForAll(
            FilterExpressionGenerators.ArbitraryPropertyName(),
            FilterExpressionGenerators.ArbitraryComparisonOperator(),
            FilterExpressionGenerators.ArbitraryLiteralValue(),
            (property, op, value) =>
            {
                var left = FilterExpression.Property(property);
                var right = FilterExpression.Literal(value);
                var filter = FilterExpression.Comparison(left, op, right);

                return filter.Type == FilterExpressionType.Comparison &&
                       filter.Operator == op &&
                       filter.Left!.Property == property &&
                       Equals(filter.Right!.Value, value);
            });
    }

    [UnitTest]
    public Property LogicalFilter_ShouldCombineExpressions()
    {
        return Prop.ForAll(
            FilterExpressionGenerators.ArbitrarySimpleFilter(),
            FilterExpressionGenerators.ArbitrarySimpleFilter(),
            FilterExpressionGenerators.ArbitraryLogicalOperator(),
            (left, right, op) =>
            {
                var filter = FilterExpression.Logical(left, op, right);

                return filter.Type == FilterExpressionType.Logical &&
                       filter.Operator == op &&
                       filter.Left == left &&
                       filter.Right == right;
            });
    }

    [UnitTest]
    public void Property_WithEmptyString_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Property("");
        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Property_WithWhitespaceString_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Property("   ");
        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Property_WithNullString_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Property(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Literal_WithNullValue_ShouldCreateFilter()
    {
        // Arrange & Act
        var filter = FilterExpression.Literal(null);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Literal);
        filter.Value.Should().BeNull();
    }

    [UnitTest]
    public void Literal_WithComplexObject_ShouldPreserveObject()
    {
        // Arrange
        var complexObject = new Dictionary<string, object>
        {
            ["nested"] = new { Value = 42 },
            ["array"] = new[] { 1, 2, 3 }
        };

        // Act
        var filter = FilterExpression.Literal(complexObject);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Literal);
        filter.Value.Should().Be(complexObject);
    }

    [UnitTest]
    public void Comparison_WithNullLeft_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Comparison(null!, FilterOperator.Equal, FilterExpression.Literal(1));
        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Comparison_WithNullRight_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Comparison(FilterExpression.Property("test"), FilterOperator.Equal, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Logical_WithNullLeft_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Logical(null!, FilterOperator.And, FilterExpression.Literal(true));
        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Logical_WithNullRight_ShouldThrowException()
    {
        // Arrange & Act & Assert
        Action act = () => FilterExpression.Logical(FilterExpression.Literal(true), FilterOperator.And, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [UnitTest]
    public void Complex_NestedFilter_ShouldBuildCorrectly()
    {
        // Arrange
        var nameFilter = FilterExpression.Comparison(
            FilterExpression.Property("name"),
            FilterOperator.Equal,
            FilterExpression.Literal("John"));

        var ageFilter = FilterExpression.Comparison(
            FilterExpression.Property("age"),
            FilterOperator.GreaterThan,
            FilterExpression.Literal(18));

        var statusFilter = FilterExpression.Comparison(
            FilterExpression.Property("status"),
            FilterOperator.Equal,
            FilterExpression.Literal("active"));

        // Act
        var combinedFilter = FilterExpression.Logical(
            FilterExpression.Logical(nameFilter, FilterOperator.And, ageFilter),
            FilterOperator.And,
            statusFilter);

        // Assert
        combinedFilter.Type.Should().Be(FilterExpressionType.Logical);
        combinedFilter.Operator.Should().Be(FilterOperator.And);

        var leftPart = combinedFilter.Left!;
        leftPart.Type.Should().Be(FilterExpressionType.Logical);
        leftPart.Operator.Should().Be(FilterOperator.And);

        var rightPart = combinedFilter.Right!;
        rightPart.Type.Should().Be(FilterExpressionType.Comparison);
        rightPart.Left!.Property.Should().Be("status");
    }

    [UnitTest]
    public void In_Operator_ShouldHandleMultipleValues()
    {
        // Arrange
        var values = new object[] { "apple", "banana", "cherry" };
        var property = FilterExpression.Property("fruit");
        var literal = FilterExpression.Literal(values);

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.In, literal);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.In);
        filter.Left!.Property.Should().Be("fruit");
        filter.Right!.Value.Should().BeEquivalentTo(values);
    }

    [UnitTest]
    public void Between_Operator_ShouldHandleRange()
    {
        // Arrange
        var property = FilterExpression.Property("price");
        var range = new object[] { 10, 100 };
        var literal = FilterExpression.Literal(range);

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.Between, literal);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.Between);
        filter.Left!.Property.Should().Be("price");
        filter.Right!.Value.Should().BeEquivalentTo(range);
    }

    [UnitTest]
    public void Like_Operator_ShouldHandlePatterns()
    {
        // Arrange
        var property = FilterExpression.Property("name");
        var pattern = FilterExpression.Literal("%john%");

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.Like, pattern);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.Like);
        filter.Left!.Property.Should().Be("name");
        filter.Right!.Value.Should().Be("%john%");
    }

    [UnitTest]
    public void IsNull_Operator_ShouldWork()
    {
        // Arrange
        var property = FilterExpression.Property("description");
        var nullValue = FilterExpression.Literal(null);

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.IsNull, nullValue);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.IsNull);
        filter.Left!.Property.Should().Be("description");
        filter.Right!.Value.Should().BeNull();
    }

    [UnitTest]
    public void IsNotNull_Operator_ShouldWork()
    {
        // Arrange
        var property = FilterExpression.Property("description");
        var nullValue = FilterExpression.Literal(null);

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.IsNotNull, nullValue);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.IsNotNull);
        filter.Left!.Property.Should().Be("description");
    }

    [UnitTest]
    public void Spatial_Intersects_ShouldWork()
    {
        // Arrange
        var property = FilterExpression.Property("geometry");
        var geometry = FilterExpression.Literal("POINT(1 1)");

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.Intersects, geometry);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.Intersects);
        filter.Left!.Property.Should().Be("geometry");
        filter.Right!.Value.Should().Be("POINT(1 1)");
    }

    [UnitTest]
    public void Spatial_Within_ShouldWork()
    {
        // Arrange
        var property = FilterExpression.Property("geometry");
        var polygon = FilterExpression.Literal("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))");

        // Act
        var filter = FilterExpression.Comparison(property, FilterOperator.Within, polygon);

        // Assert
        filter.Type.Should().Be(FilterExpressionType.Comparison);
        filter.Operator.Should().Be(FilterOperator.Within);
        filter.Left!.Property.Should().Be("geometry");
        filter.Right!.Value.Should().Be("POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))");
    }

    [UnitTest]
    public void DeepNesting_ShouldNotCauseStackOverflow()
    {
        // Arrange - Create deeply nested filter
        FilterExpression filter = FilterExpression.Literal(true);

        for (int i = 0; i < 1000; i++)
        {
            var newFilter = FilterExpression.Comparison(
                FilterExpression.Property($"prop_{i}"),
                FilterOperator.Equal,
                FilterExpression.Literal(i));

            filter = FilterExpression.Logical(filter, FilterOperator.And, newFilter);
        }

        // Act & Assert - Should not throw
        filter.Type.Should().Be(FilterExpressionType.Logical);
    }
}
