// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class FilterExpressionNormalizerTests
{
    [UnitTest]
    public void Normalize_DateTimeTextWithoutOffset_AssumesUtc()
    {
        var layer = new LayerDefinition(
            Id: 1,
            Name: "Temporal Layer",
            Description: null,
            GeometryType: GeometryType.None,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("event_time", FieldType.DateTime)
            ]);

        var expression = new BinaryExpression(
            new PropertyReference("event_time"),
            BinaryOperator.Equal,
            new Literal("2024-02-16T10:00:00", LiteralType.Text));

        var normalized = FilterExpressionNormalizer.Normalize(expression, layer);

        var binary = normalized.Should().BeOfType<BinaryExpression>().Subject;
        var literal = binary.Right.Should().BeOfType<Literal>().Subject;
        literal.Type.Should().Be(LiteralType.DateTime);

        var value = literal.Value.Should().BeOfType<DateTimeOffset>().Subject;
        value.Offset.Should().Be(TimeSpan.Zero);
        value.UtcDateTime.Should().Be(new DateTime(2024, 2, 16, 10, 0, 0, DateTimeKind.Utc));
    }

    [UnitTest]
    public void Normalize_DeeplyNestedExpression_ThrowsArgumentException()
    {
        var layer = new LayerDefinition(
            Id: 1,
            Name: "Nested Layer",
            Description: null,
            GeometryType: GeometryType.None,
            SpatialReference: SpatialReference.WGS84,
            Fields:
            [
                new FieldDefinition(FieldNames.ObjectId, FieldType.Integer, Nullable: false),
                new FieldDefinition("name", FieldType.String, Length: 255),
                new FieldDefinition("age", FieldType.Integer)
            ]);

        FilterExpression expression = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Equal,
            new Literal("root", LiteralType.Text));

        for (var i = 0; i < FilterExpressionNormalizer.MaxExpressionDepth + 1; i++)
        {
            expression = new BinaryExpression(
                expression,
                BinaryOperator.And,
                new BinaryExpression(
                    new PropertyReference("age"),
                    BinaryOperator.GreaterThan,
                    new Literal(i, LiteralType.Number)));
        }

        var act = () => FilterExpressionNormalizer.Normalize(expression, layer);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {FilterExpressionNormalizer.MaxExpressionDepth}*");
    }
}
