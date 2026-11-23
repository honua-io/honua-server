// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Shared.Models;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters.Fes20;

public sealed class Fes20MatchCaseTests
{
    [UnitTheory]
    [InlineData("PropertyIsEqualTo", BinaryOperator.Equal)]
    [InlineData("PropertyIsNotEqualTo", BinaryOperator.NotEqual)]
    [InlineData("PropertyIsLessThan", BinaryOperator.LessThan)]
    [InlineData("PropertyIsGreaterThanOrEqualTo", BinaryOperator.GreaterThanOrEqual)]
    public void ParseFilter_ComparisonWithMatchCaseFalse_WrapsBothOperandsInCasei(string element, BinaryOperator expected)
    {
        var binary = ParseComparison(element, "matchCase=\"false\"", "<fes:Literal>Zürich</fes:Literal>");

        binary.Operator.Should().Be(expected);
        CaseiOperand(binary.Left).Should().Be(new PropertyReference("name"));
        CaseiOperand(binary.Right).Should().Be(new Literal("Zürich", LiteralType.Text));
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("matchCase=\"true\"")]
    public void ParseFilter_ComparisonWithoutMatchCaseFalse_StaysCaseSensitive(string attribute)
    {
        var binary = ParseComparison("PropertyIsEqualTo", attribute, "<fes:Literal>Zürich</fes:Literal>");

        binary.Left.Should().Be(new PropertyReference("name"));
        binary.Right.Should().Be(new Literal("Zürich", LiteralType.Text));
    }

    [UnitTest]
    public void ParseFilter_TypedNumericLiteralWithMatchCaseFalse_IsNotWrapped()
    {
        var binary = ParseComparison("PropertyIsEqualTo", "matchCase=\"false\"", "<fes:Literal type=\"xs:int\">5</fes:Literal>");

        binary.Left.Should().Be(new PropertyReference("name"));
        binary.Right.Should().Be(new Literal(5, LiteralType.Number));
    }

    [UnitTheory]
    [InlineData("<fes:Literal>a</fes:Literal><fes:Literal>A</fes:Literal>")]
    [InlineData("<fes:ValueReference>name</fes:ValueReference><fes:ValueReference>alias</fes:ValueReference>")]
    public void ParseFilter_MatchCaseFalseWithoutPropertyAndTextLiteral_IsNotWrapped(string operands)
    {
        var binary = Fes20Parser.ParseFilter(
                "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:PropertyIsEqualTo matchCase=\"false\">"
                + operands + "</fes:PropertyIsEqualTo></fes:Filter>")
            .Should().BeOfType<BinaryExpression>().Subject;

        binary.Left.Should().NotBeOfType<FunctionCall>();
        binary.Right.Should().NotBeOfType<FunctionCall>();
    }

    [UnitTest]
    public void Normalize_MatchCaseFalseOnStringField_KeepsCaseInsensitiveComparison()
    {
        var normalized = Normalize("<fes:Literal>ZÜRICH</fes:Literal>", MetadataV2FieldType.String);

        CaseiOperand(normalized.Left).Should().Be(new PropertyReference("name"));
        CaseiOperand(normalized.Right).Should().Be(new Literal("ZÜRICH", LiteralType.Text));
    }

    [UnitTheory]
    [InlineData(MetadataV2FieldType.Integer, "5", 5)]
    [InlineData(MetadataV2FieldType.Boolean, "true", true)]
    public void Normalize_MatchCaseFalseOnNonStringField_UsesPlainTypedComparison(
        MetadataV2FieldType fieldType, string text, object expected)
    {
        var normalized = Normalize($"<fes:Literal>{text}</fes:Literal>", fieldType);

        normalized.Left.Should().Be(new PropertyReference("name"));
        normalized.Right.Should().BeOfType<Literal>().Which.Value.Should().Be(expected);
    }

    [UnitTest]
    public void Normalize_Cql2CaseiOnIntegerField_UsesPlainNumericComparison()
    {
        var expression = new BinaryExpression(
            new FunctionCall("CASEI", [new PropertyReference("name")]),
            BinaryOperator.Equal,
            new FunctionCall("CASEI", [new Literal("5", LiteralType.Text)]));

        var normalized = FilterExpressionNormalizer.Normalize(expression, ResourceWithNameField(MetadataV2FieldType.Integer))
            .Should().BeOfType<BinaryExpression>().Subject;

        normalized.Left.Should().Be(new PropertyReference("name"));
        normalized.Right.Should().Be(new Literal(5, LiteralType.Number));
    }

    private static BinaryExpression ParseComparison(string element, string attribute, string literal)
        => Fes20Parser.ParseFilter(
                $"<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\"><fes:{element} {attribute}>"
                + $"<fes:ValueReference>name</fes:ValueReference>{literal}</fes:{element}></fes:Filter>")
            .Should().BeOfType<BinaryExpression>().Subject;

    private static BinaryExpression Normalize(string literal, MetadataV2FieldType fieldType)
    {
        return FilterExpressionNormalizer.Normalize(
                ParseComparison("PropertyIsEqualTo", "matchCase=\"false\"", literal), ResourceWithNameField(fieldType))
            .Should().BeOfType<BinaryExpression>().Subject;
    }

    private static MetadataV2Object ResourceWithNameField(MetadataV2FieldType fieldType)
        => new()
        {
            Metadata = new MetadataV2ObjectMetadata { Id = "places", Name = "Places" },
            Type = MetadataV2ObjectType.FeatureDataset,
            SchemaFields = [new MetadataV2Field { Name = "name", Type = fieldType }],
        };

    private static FilterExpression CaseiOperand(FilterExpression expression)
    {
        var function = expression.Should().BeOfType<FunctionCall>().Subject;
        function.FunctionName.Should().Be("CASEI");
        return function.Arguments.Should().ContainSingle().Subject;
    }
}
