// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class FilterExpressionNormalizerTests
{
    [UnitTest]
    public void Normalize_DateTimeTextWithoutOffset_AssumesUtc()
    {
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-temporal", Name = "Temporal Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "event_time", Type = MetadataV2FieldType.DateTime },
            ],
        };

        var expression = new BinaryExpression(
            new PropertyReference("event_time"),
            BinaryOperator.Equal,
            new Literal("2024-02-16T10:00:00", LiteralType.Text));

        var normalized = FilterExpressionNormalizer.Normalize(expression, resource);

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
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-nested", Name = "Nested Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "age", Type = MetadataV2FieldType.Integer },
            ],
        };

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

        var act = () => FilterExpressionNormalizer.Normalize(expression, resource);

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {FilterExpressionNormalizer.MaxExpressionDepth}*");
    }
}
