// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Filtering;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Stac;

[Protocol(TestProtocols.Stac)]
public sealed class Cql2FilterProcessorCrossCollectionTests
{
    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialPredicate_CollapsesToUnknown()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            Geometry());

        AssertTypedMissingSemantics(predicate);
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialDistanceOperand_CollapsesToUnknown()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new Literal(10, LiteralType.Number));

        AssertTypedMissingSemantics(predicate);
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialDistanceValue_CollapsesToUnknown()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new PropertyReference("distance_field"));

        AssertTypedMissingSemantics(predicate);
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_TemporalPredicate_CollapsesToUnknown()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("collection_time"),
            new Literal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), LiteralType.DateTime));

        AssertTypedMissingSemantics(predicate);
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_ArrayPredicate_CollapsesToUnknown()
    {
        var predicate = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("tags"),
            new ArrayLiteral([new Literal("featured", LiteralType.Text)]));

        AssertTypedMissingSemantics(predicate);
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_UserAuthoredTypedNull_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new Literal(null, LiteralType.Null),
            Geometry());

        var rewritten = Rewrite(predicate);

        rewritten.Should().BeOfType<SpatialPredicate>();
    }

    private static void AssertTypedMissingSemantics(FilterExpression predicate)
    {
        var rewrittenPredicate = Rewrite(predicate);
        var nullLiteral = rewrittenPredicate.Should().BeOfType<Literal>().Which;
        nullLiteral.Type.Should().Be(LiteralType.Null);

        var matchingBranch = new BinaryExpression(
            new PropertyReference("name"),
            BinaryOperator.Equal,
            new Literal("item-b", LiteralType.Text));
        var rewrittenOr = Rewrite(new BinaryExpression(predicate, BinaryOperator.Or, matchingBranch));

        InMemoryFilterEvaluator.Evaluate(rewrittenPredicate, Properties()).Should().BeFalse();
        InMemoryFilterEvaluator.Evaluate(rewrittenOr, Properties()).Should().BeTrue();
    }

    private static FilterExpression Rewrite(FilterExpression expression)
        => Cql2FilterProcessor.ApplyCrossCollectionNullSemantics(
            expression,
            Resource("local", new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }),
            [Resource(
                "remote",
                new MetadataV2Field { Name = "footprint", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "distance_field", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "collection_time", Type = MetadataV2FieldType.DateTime },
                new MetadataV2Field { Name = "tags", Type = MetadataV2FieldType.Json })]);

    private static MetadataV2Resource Resource(string id, params MetadataV2Field[] fields) => new()
    {
        Metadata = new MetadataV2ObjectMetadata { Id = id, Name = id },
        SchemaFields = fields
    };

    private static GeometryLiteral Geometry() => new([1, 2, 3], 4326, "test");

    private static Dictionary<string, JsonElement> Properties()
    {
        using var document = JsonDocument.Parse("""{"name":"item-b"}""");
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(
                static property => property.Name,
                static property => property.Value.Clone(),
                StringComparer.Ordinal);
    }
}
