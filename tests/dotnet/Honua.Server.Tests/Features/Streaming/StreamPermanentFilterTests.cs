// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Honua.Server.Features.Streaming;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Streaming;

[Protocol(TestProtocols.Streaming)]
public sealed class StreamPermanentFilterTests
{
    [UnitTest]
    public void AppendPermanentFilterPredicate_ComparableFilter_AddsPredicate()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("classification <> 'secret'"),
            Filters(),
            predicates);

        act.Should().NotThrow();
        predicates.Should().ContainSingle();
        var security = new StreamSubscriberSecurity(
            new HashSet<string>(StringComparer.Ordinal) { "svc" },
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>
            {
                [("svc", 1)] = new StreamLayerReadPolicy(predicates, new HashSet<string>(StringComparer.OrdinalIgnoreCase))
            },
            new Dictionary<(string Service, int Layer), StreamLayerReadPolicy>());
        security.Allows(Envelope("public")).Should().BeTrue();
        security.Allows(Envelope("secret")).Should().BeFalse();
    }

    [UnitTest]
    public void AppendPermanentFilterPredicate_UnsupportedLanguage_Throws()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("classification <> 'secret'", "not-a-language"),
            Filters(),
            predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void AppendPermanentFilterPredicate_ExcessiveDepth_Throws()
    {
        var predicates = new List<FilterExpression>();
        var expression = string.Join(
            " AND ",
            Enumerable.Repeat("classification <> 'a'", InMemoryFilterEvaluator.MaxStreamingDepth + 2));

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression),
            Filters(),
            predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void AppendPermanentFilterPredicate_NoSavedFilter_LeavesPredicatesUntouched()
    {
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(null),
            Filters(),
            predicates);

        act.Should().NotThrow();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void AppendPermanentFilterPredicate_DifferentFieldCasing_UsesCanonicalName()
    {
        var predicates = new List<FilterExpression>();
        FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("CLASSIFICATION IS NULL"), Filters(), predicates);

        var predicate = predicates.Should().ContainSingle().Subject;
        var expression = predicate.Should().BeOfType<UnaryExpression>().Subject;
        expression.Operand.Should().Be(new PropertyReference("classification"));
        InMemoryFilterEvaluator.Evaluate(predicate, Envelope("secret").Attributes!).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.BigInteger, "classification IN (1, 9007199254740993)", "9007199254740993")]
    [InlineData(MetadataV2FieldType.Boolean, "classification = TRUE", "true")]
    public void AppendPermanentFilterPredicate_ComparableScalar_EvaluatesConsistently(
        MetadataV2FieldType fieldType, string expression, string jsonValue)
    {
        var predicates = new List<FilterExpression>();
        FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression, fieldType: fieldType), Filters(), predicates);
        var attributes = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["classification"] = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(jsonValue)
        };

        InMemoryFilterEvaluator.Evaluate(predicates.Should().ContainSingle().Subject, attributes).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.DateTime)]
    [InlineData(MetadataV2FieldType.Date)]
    [InlineData(MetadataV2FieldType.Time)]
    public void AppendPermanentFilterPredicate_TemporalField_Throws(MetadataV2FieldType fieldType)
    {
        var resource = Resource("classification IS NULL", fieldType: fieldType);
        var predicates = new List<FilterExpression>();

        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(resource, Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("classification NOT IN (9007199254740992, 0.5)")]
    [InlineData("classification <> 0.5")]
    public void AppendPermanentFilterPredicate_FloatingLiteral_Throws(string expression)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression, fieldType: MetadataV2FieldType.BigInteger), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("classification < 'Ａ'")]
    [InlineData("classification <= 'a'")]
    [InlineData("classification > 'a'")]
    [InlineData("classification >= 'a'")]
    public void AppendPermanentFilterPredicate_OrderingComparison_Throws(string expression)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("classification = 1")]
    [InlineData("classification NOT IN ('a', 1)")]
    [InlineData("classification <> TRUE")]
    public void AppendPermanentFilterPredicate_MixedOperandTypes_Throws(string expression)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Double)]
    [InlineData(MetadataV2FieldType.Float)]
    public void AppendPermanentFilterPredicate_FloatingPointField_Throws(MetadataV2FieldType fieldType)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("classification <> 9007199254740993", fieldType: fieldType), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("classification LIKE 'secret%'")]
    [InlineData("classification NOT LIKE 'secret%'")]
    [InlineData("NOT (classification LIKE 'secret%')")]
    public void AppendPermanentFilterPredicate_PatternComparison_Throws(string expression)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("classification <> 'AABBCCDD-1122-3344-5566-778899AABBCC'")]
    [InlineData("classification IS NULL")]
    public void AppendPermanentFilterPredicate_UuidField_Throws(string expression)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource(expression, fieldType: MetadataV2FieldType.Uuid), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTheory]
    [InlineData("OBJECTID")]
    [InlineData("object_id")]
    [InlineData("id")]
    [InlineData("LAYERID")]
    [InlineData("layer_id")]
    [InlineData("geom")]
    [InlineData("GEOMETRY")]
    [InlineData("shape")]
    public void AppendPermanentFilterPredicate_SystemField_Throws(string fieldName)
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource($"{fieldName} IS NULL", fieldType: MetadataV2FieldType.Integer, fieldName: fieldName),
            Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    [UnitTest]
    public void AppendPermanentFilterPredicate_UnknownField_Throws()
    {
        var predicates = new List<FilterExpression>();
        var act = () => FeatureStreamEndpoints.AppendPermanentFilterPredicate(
            Resource("missing_field IS NULL"), Filters(), predicates);

        act.Should().Throw<InvalidOperationException>();
        predicates.Should().BeEmpty();
    }

    private static FilterExpressionService Filters()
        => new FilterExpressionService(new FilterExpressionTranslator(Substitute.For<ISqlFilterTranslator>()));

    private static MetadataV2Object Resource(
        string? expression,
        string? language = null,
        MetadataV2FieldType fieldType = MetadataV2FieldType.String,
        string fieldName = "classification")
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "roads", Name = "roads" },
            SchemaFields =
            [
                new MetadataV2Field { Name = fieldName, Type = fieldType }
            ],
            PermanentFilter = expression is null
                ? null
                : new MetadataV2PermanentFilter
                {
                    Expression = expression,
                    Language = language ?? MetadataV2PermanentFilterLanguages.GeoServicesSql
                }
        };

    private static FeatureStreamEnvelope Envelope(string classification)
        => new()
        {
            EventId = "event",
            Cursor = 1,
            Timestamp = DateTimeOffset.UnixEpoch,
            Protocol = "rest",
            RequestId = "request",
            ServiceId = "svc",
            LayerId = 1,
            ObjectId = 10,
            FeatureId = "10",
            Operation = "update",
            Attributes = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["classification"] = System.Text.Json.JsonSerializer.SerializeToElement(classification)
            }
        };
}
