// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class FilterExpressionNormalizerTests
{
    [UnitTest]
    public void SRV_DB_007_StringFieldPreservesUntypedFesLiteralLexicalValue()
    {
        var expression = Fes20Parser.ParseFilter(
            "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo><fes:ValueReference>zip</fes:ValueReference><fes:Literal>02134</fes:Literal></fes:PropertyIsEqualTo></fes:Filter>");

        var normalized = FilterExpressionNormalizer.Normalize(
            expression, ResourceWithField("zip", MetadataV2FieldType.String));

        var literal = normalized.Should().BeOfType<BinaryExpression>().Subject.Right
            .Should().BeOfType<Literal>().Subject;
        literal.Type.Should().Be(LiteralType.Text);
        literal.Value.Should().Be("02134");
    }

    [UnitTest]
    public void SRV_DB_017_TimeFieldCoercesTextLiteralToTimeOnly()
    {
        var expression = new BinaryExpression(
            new PropertyReference("open_time"), BinaryOperator.Equal,
            new Literal("08:00:00", LiteralType.Text));

        var normalized = FilterExpressionNormalizer.Normalize(
            expression, ResourceWithField("open_time", MetadataV2FieldType.Time));

        normalized.Should().BeOfType<BinaryExpression>().Subject.Right
            .Should().BeOfType<Literal>().Subject.Value.Should().Be(new TimeOnly(8, 0));
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Integer, "1000", 1000)]
    [InlineData(MetadataV2FieldType.BigInteger, "5000000000", 5000000000L)]
    [InlineData(MetadataV2FieldType.Float, "12.5", 12.5d)]
    [InlineData(MetadataV2FieldType.Double, "-3", -3)]
    public void SRV_DB_007_NumericFieldCoercesUntypedFesLiteral(MetadataV2FieldType type, string lexical, object expected)
    {
        var expression = Fes20Parser.ParseFilter(
            $"<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsGreaterThan><fes:ValueReference>pop</fes:ValueReference><fes:Literal>{lexical}</fes:Literal></fes:PropertyIsGreaterThan></fes:Filter>");

        var normalized = FilterExpressionNormalizer.Normalize(expression, ResourceWithField("pop", type));

        var literal = normalized.Should().BeOfType<BinaryExpression>().Subject.Right
            .Should().BeOfType<Literal>().Subject;
        literal.Type.Should().Be(LiteralType.Number);
        literal.Value.Should().Be(expected);
    }

    [UnitTest]
    public void SRV_DB_007_NumericFieldRejectsNonNumericText()
    {
        var expression = new BinaryExpression(
            new PropertyReference("pop"), BinaryOperator.Equal, new Literal("abc", LiteralType.Text));

        var act = () => FilterExpressionNormalizer.Normalize(
            expression, ResourceWithField("pop", MetadataV2FieldType.Integer));

        act.Should().Throw<ArgumentException>().WithMessage("*expects a numeric value*");
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Uuid, "3F2504E0-4F89-11D3-9A0C-0305E82C3301", "\"3f2504e0-4f89-11d3-9a0c-0305e82c3301\"")]
    [InlineData(MetadataV2FieldType.Time, "08:00:00", "\"08:00:00\"")]
    public void SRV_DB_017_NormalizedUuidAndTimeLiteralsStillMatchInMemory(MetadataV2FieldType type, string lexical, string json)
    {
        var expression = new BinaryExpression(
            new PropertyReference("f"), BinaryOperator.Equal, new Literal(lexical, LiteralType.Text));

        var normalized = FilterExpressionNormalizer.Normalize(expression, ResourceWithField("f", type));

        using var document = System.Text.Json.JsonDocument.Parse(json);
        InMemoryFilterEvaluator.Evaluate(
            normalized,
            new Dictionary<string, System.Text.Json.JsonElement> { ["f"] = document.RootElement })
            .Should().BeTrue();
    }

    private static MetadataV2Resource ResourceWithField(string name, MetadataV2FieldType type)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "audit", Name = "Audit" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields = [new MetadataV2Field { Name = name, Type = type }],
        };

    [UnitTest]
    public void EnsureWithinBounds_NodeCountWithinCap_DoesNotThrow()
    {
        // One array node plus four elements.
        var expression = new ArrayLiteral(Literals(4));

        var act = () => FilterExpressionNormalizer.EnsureWithinBounds(expression, maxNodes: 5);

        act.Should().NotThrow();
    }

    [UnitTest]
    public void EnsureWithinBounds_WideNodeBeyondCap_StopsQueuingChildrenAtTheCap()
    {
        var elements = new CountingList(Literals(1_000));
        var expression = new ArrayLiteral(elements);

        var act = () => FilterExpressionNormalizer.EnsureWithinBounds(expression, maxNodes: 5);

        act.Should().Throw<ArgumentException>().WithMessage("*maximum size of 5 nodes*");
        elements.Enumerated.Should().BeLessThanOrEqualTo(5);
    }

    private static FilterExpression[] Literals(int count)
        => Enumerable.Range(0, count)
            .Select(static i => (FilterExpression)new Literal(i, LiteralType.Number))
            .ToArray();

    private sealed class CountingList(IReadOnlyList<FilterExpression> inner) : IReadOnlyList<FilterExpression>
    {
        public int Enumerated { get; private set; }

        public int Count => inner.Count;

        public FilterExpression this[int index] => inner[index];

        public IEnumerator<FilterExpression> GetEnumerator()
        {
            foreach (var item in inner)
            {
                Enumerated++;
                yield return item;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

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
    public void Normalize_GeodesicSpatialPredicate_PreservesGeodesicFlag()
    {
        // The OData parser marks geo.intersects predicates Geodesic; normalization
        // rebuilds the node and must not reset the protocol marker, otherwise the
        // SQL translator would silently fall back to planar semantics.
        var resource = new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "res-spatial", Name = "Spatial Layer" },
            Type = MetadataV2ResourceType.FeatureDataset,
            SchemaFields =
            [
                new MetadataV2Field { Name = FieldNames.ObjectId, Type = MetadataV2FieldType.Integer, Nullable = false },
                new MetadataV2Field { Name = "geom", Type = MetadataV2FieldType.Geometry },
            ],
        };

        var expression = new SpatialPredicate(
            SpatialOperator.Intersects,
            new PropertyReference("geom"),
            new GeometryLiteral([1, 2, 3, 4], 4326, "POINT(1 2)"))
        {
            Geodesic = true
        };

        var normalized = FilterExpressionNormalizer.Normalize(expression, resource);

        var spatial = normalized.Should().BeOfType<SpatialPredicate>().Subject;
        spatial.Geodesic.Should().BeTrue();
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
