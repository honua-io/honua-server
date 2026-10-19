// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Cql2;
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

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NonStrictFunction_PreservesLocalFallback()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new FunctionCall(
                "COALESCE",
                [new PropertyReference("remote_time"), new PropertyReference("local_time")]),
            new Literal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), LiteralType.DateTime));

        var rewritten = Rewrite(predicate).Should().BeOfType<TemporalPredicate>().Which;
        var coalesce = rewritten.Left.Should().BeOfType<FunctionCall>().Which;
        coalesce.Arguments[0].Should().BeOfType<Literal>().Which.Type.Should().Be(LiteralType.Null);
        coalesce.Arguments[1].Should().Be(new PropertyReference("local_time"));
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialPredicateWithGlobalUnknown_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            new PropertyReference("globally_unknown_geometry"));

        Rewrite(predicate).Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialDistanceWithGlobalUnknown_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new PropertyReference("globally_unknown_distance"));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_TemporalPredicateWithGlobalUnknown_RemainsForValidation()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new PropertyReference("globally_unknown_time"));

        Rewrite(predicate).Should().BeOfType<TemporalPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_ArrayPredicateWithGlobalUnknown_RemainsForValidation()
    {
        var predicate = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("tags"),
            new PropertyReference("globally_unknown_array"));

        Rewrite(predicate).Should().BeOfType<ArrayPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SubstitutionWithAuthoredInvalidSibling_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            new Literal(null, LiteralType.Null));

        Rewrite(predicate).Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_ArrayLiteralContainingNull_DoesNotVetoCollapse()
    {
        var predicate = new ArrayPredicate(
            ArrayOperator.Contains,
            new PropertyReference("tags"),
            new ArrayLiteral([new Literal(null, LiteralType.Null)]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CoalesceContainingNull_DoesNotVetoSiblingCollapse()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "COALESCE",
                [new Literal(null, LiteralType.Null), new PropertyReference("local_time")]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_KnownStrictFunction_PropagatesSubstitutedNull()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new FunctionCall("GEOLENGTH", [new PropertyReference("remote_geometry")]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NestedTypedCql2Json_DoesNotMaskInvalidOuterOperand()
    {
        const string json =
            """{"op":"s_intersects","args":[{"op":"s_intersects","args":[{"property":"footprint"},{"type":"Point","coordinates":[1,2]}]},{"type":"Point","coordinates":[3,4]}]}""";
        var predicate = new Cql2JsonParser().Parse(json);

        var rewritten = Rewrite(predicate).Should().BeOfType<SpatialPredicate>().Which;
        rewritten.Left.Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_StrictFunctionWithInvalidArity_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new FunctionCall(
                "GEOLENGTH",
                [new PropertyReference("remote_geometry"), new Literal(null, LiteralType.Null)]));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_StrictFunctionWithWrongInputKind_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new FunctionCall("GEOLENGTH", [new PropertyReference("distance_field")]));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NumericStrictFunctionInGeometrySlot_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new FunctionCall("GEOLENGTH", [new PropertyReference("remote_geometry")]),
            Geometry());

        Rewrite(predicate).Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_RemoteNumericInGeometrySlot_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("distance_field"),
            Geometry());

        Rewrite(predicate).Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_AuthoredNullDistance_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new Literal(null, LiteralType.Null));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_GeoDistance_PropagatesNumericNull()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new FunctionCall("GEODISTANCE", [new PropertyReference("remote_geometry"), Geometry()]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CurrentTimestampSibling_PreservesValidTemporalCollapse()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall("CURRENT_TIMESTAMP", []));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialFunctionSibling_PreservesValidGeometryCollapse()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            new FunctionCall(
                "ST_BUFFER",
                [new PropertyReference("local_geometry"), new Literal(10, LiteralType.Number)]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_UnknownFunctionInsideCoalesce_RemainsForValidation()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "COALESCE",
                [new FunctionCall("UNKNOWN_TEMPORAL", []), new PropertyReference("local_time")]));

        Rewrite(predicate).Should().BeOfType<TemporalPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_SpatialFunctionWithWrongInputKind_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            new FunctionCall(
                "ST_BUFFER",
                [new PropertyReference("local_geometry"), new PropertyReference("local_geometry")]));

        Rewrite(predicate).Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_ConflictingRemoteFieldKinds_RemainsForValidation()
    {
        var predicate = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("footprint"),
            Geometry());
        var local = Resource(
            "local",
            new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String });
        var remotes = new[]
        {
            Resource(
                "remote-geometry",
                new MetadataV2Field { Name = "footprint", Type = MetadataV2FieldType.Geometry }),
            Resource(
                "remote-number",
                new MetadataV2Field { Name = "footprint", Type = MetadataV2FieldType.Double })
        };

        var rewritten = Cql2FilterProcessor.ApplyCrossCollectionNullSemantics(predicate, local, remotes);

        rewritten.Should().BeOfType<SpatialPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_ArithmeticDistanceSibling_PreservesValidCollapse()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new BinaryExpression(
                new PropertyReference("local_distance"),
                BinaryOperator.Add,
                new Literal(1, LiteralType.Number)));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CastTemporalSibling_PreservesValidCollapse()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CAST",
                [new PropertyReference("local_text"), new Literal("TIMESTAMP", LiteralType.Text)]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NullIfNumericSibling_PreservesValidCollapse()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new FunctionCall(
                "NULLIF",
                [new PropertyReference("local_distance"), new Literal(0, LiteralType.Number)]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NullIfFirstOperand_PropagatesSubstitutedNull()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new FunctionCall(
                "NULLIF",
                [new PropertyReference("distance_field"), new Literal(0, LiteralType.Number)]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CaseTemporalSibling_PreservesValidCollapse()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CASE",
                [
                    new Literal(true, LiteralType.Boolean),
                    new PropertyReference("local_time"),
                    new Literal(
                        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                        LiteralType.DateTime)
                ]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_InvalidCastTarget_RemainsForValidation()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CAST",
                [new PropertyReference("local_text"), new Literal("TIMESTAMP(bad)", LiteralType.Text)]));

        Rewrite(predicate).Should().BeOfType<TemporalPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NullIfWithMismatchedKinds_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new FunctionCall(
                "NULLIF",
                [new PropertyReference("local_distance"), new PropertyReference("local_text")]));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CaseWithNonBooleanCondition_RemainsForValidation()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CASE",
                [new Literal(1, LiteralType.Number), new PropertyReference("local_time")]));

        Rewrite(predicate).Should().BeOfType<TemporalPredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NegatedRemoteNumeric_PropagatesSubstitutedNull()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            Geometry(),
            Geometry(),
            new UnaryExpression(
                UnaryOperator.Negate,
                new PropertyReference("distance_field")));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NegatedWrongKind_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new UnaryExpression(
                UnaryOperator.Negate,
                new PropertyReference("local_text")));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_NullIfAuthoredNullFirstOperand_InfersComparisonKind()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new FunctionCall(
                "NULLIF",
                [new Literal(null, LiteralType.Null), new PropertyReference("local_distance")]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_CaseAuthoredNullCondition_IsBooleanUnknown()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CASE",
                [new Literal(null, LiteralType.Null), new PropertyReference("local_time")]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_InvalidLogicalConditions_RemainForValidation()
    {
        FilterExpression[] invalidConditions =
        [
            new UnaryExpression(UnaryOperator.Not, new PropertyReference("local_distance")),
            new BinaryExpression(
                new PropertyReference("local_distance"),
                BinaryOperator.And,
                new Literal(true, LiteralType.Boolean)),
            new BinaryExpression(
                new PropertyReference("local_text"),
                BinaryOperator.LessThan,
                new PropertyReference("local_distance")),
            new BinaryExpression(
                new PropertyReference("local_distance"),
                BinaryOperator.Like,
                new PropertyReference("local_text"))
        ];

        foreach (var condition in invalidConditions)
        {
            var predicate = new TemporalPredicate(
                TemporalOperator.After,
                new PropertyReference("remote_time"),
                new FunctionCall("CASE", [condition, new PropertyReference("local_time")]));

            Rewrite(predicate).Should().BeOfType<TemporalPredicate>($"{condition} must remain visible to validation");
        }
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_IsNullCondition_AcceptsAnyOperandKind()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CASE",
                [
                    new UnaryExpression(UnaryOperator.IsNull, new PropertyReference("local_text")),
                    new PropertyReference("local_time")
                ]));

        Rewrite(predicate).Should().BeOfType<Literal>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_AllMissingCoalesce_CollapsesAcrossTypedBoundaries()
    {
        FilterExpression[] predicates =
        [
            new SpatialPredicate(
                SpatialOperator.Intersects,
                new FunctionCall(
                    "COALESCE",
                    [new PropertyReference("remote_geometry"), new PropertyReference("remote_geometry")]),
                Geometry()),
            new TemporalPredicate(
                TemporalOperator.After,
                new FunctionCall(
                    "COALESCE",
                    [new PropertyReference("remote_time"), new PropertyReference("remote_time")]),
                new Literal(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    LiteralType.DateTime)),
            new ArrayPredicate(
                ArrayOperator.Contains,
                new FunctionCall(
                    "COALESCE",
                    [new PropertyReference("tags"), new PropertyReference("tags")]),
                new ArrayLiteral([new Literal("featured", LiteralType.Text)]))
        ];

        foreach (var predicate in predicates)
        {
            Rewrite(predicate).Should().BeOfType<Literal>($"{predicate} is guaranteed NULL");
        }
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_AllNullCase_CollapsesAcrossTypedBoundaries()
    {
        FilterExpression[] predicates =
        [
            new SpatialPredicate(
                SpatialOperator.Intersects,
                new FunctionCall(
                    "CASE",
                    [
                        new Literal(true, LiteralType.Boolean),
                        new PropertyReference("remote_geometry"),
                        new Literal(null, LiteralType.Null)
                    ]),
                Geometry()),
            new TemporalPredicate(
                TemporalOperator.After,
                new FunctionCall(
                    "CASE",
                    [
                        new Literal(true, LiteralType.Boolean),
                        new PropertyReference("remote_time"),
                        new Literal(null, LiteralType.Null)
                    ]),
                new Literal(
                    new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                    LiteralType.DateTime)),
            new ArrayPredicate(
                ArrayOperator.Contains,
                new FunctionCall(
                    "CASE",
                    [
                        new Literal(true, LiteralType.Boolean),
                        new PropertyReference("tags"),
                        new Literal(null, LiteralType.Null)
                    ]),
                new ArrayLiteral([new Literal("featured", LiteralType.Text)]))
        ];

        foreach (var predicate in predicates)
        {
            Rewrite(predicate).Should().BeOfType<Literal>($"{predicate} is guaranteed NULL");
        }
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_AuthoredNullCoalesceThroughArithmetic_RemainsForValidation()
    {
        var predicate = new SpatialDistancePredicate(
            SpatialOperator.DWithin,
            new PropertyReference("footprint"),
            Geometry(),
            new BinaryExpression(
                new FunctionCall(
                    "COALESCE",
                    [new Literal(null, LiteralType.Null), new Literal(null, LiteralType.Null)]),
                BinaryOperator.Add,
                new PropertyReference("local_distance")));

        Rewrite(predicate).Should().BeOfType<SpatialDistancePredicate>();
    }

    [UnitTest]
    public void ApplyCrossCollectionNullSemantics_AuthoredNullCaseThroughCast_RemainsForValidation()
    {
        var predicate = new TemporalPredicate(
            TemporalOperator.After,
            new PropertyReference("remote_time"),
            new FunctionCall(
                "CAST",
                [
                    new FunctionCall(
                        "CASE",
                        [
                            new Literal(true, LiteralType.Boolean),
                            new Literal(null, LiteralType.Null),
                            new Literal(null, LiteralType.Null)
                        ]),
                    new Literal("TIMESTAMP", LiteralType.Text)
                ]));

        Rewrite(predicate).Should().BeOfType<TemporalPredicate>();
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
            Resource(
                "local",
                new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String },
                new MetadataV2Field { Name = "local_time", Type = MetadataV2FieldType.DateTime },
                new MetadataV2Field { Name = "local_geometry", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "local_distance", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "local_text", Type = MetadataV2FieldType.String }),
            [Resource(
                "remote",
                new MetadataV2Field { Name = "footprint", Type = MetadataV2FieldType.Geometry },
                new MetadataV2Field { Name = "distance_field", Type = MetadataV2FieldType.Double },
                new MetadataV2Field { Name = "collection_time", Type = MetadataV2FieldType.DateTime },
                new MetadataV2Field { Name = "remote_time", Type = MetadataV2FieldType.DateTime },
                new MetadataV2Field { Name = "remote_geometry", Type = MetadataV2FieldType.Geometry },
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
