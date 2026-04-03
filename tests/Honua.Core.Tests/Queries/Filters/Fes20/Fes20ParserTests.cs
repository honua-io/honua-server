// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.TestKit.Attributes;

namespace Honua.Core.Tests.Queries.Filters.Fes20;

public sealed class Fes20ParserTests
{
    [UnitTest]
    public void ParseFilter_WithDocumentTypeDeclaration_ThrowsParseException()
    {
        const string filterXml = """
            <!DOCTYPE filter [
              <!ENTITY xxe "boom">
            ]>
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>&xxe;</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        var act = () => Fes20Parser.ParseFilter(filterXml);

        act.Should().Throw<Fes20ParseException>();
    }

    [UnitTest]
    public void ParseFilter_PropertyIsLikeWithEscapeCharAndMatchCaseFalse_ReturnsLowerLikeExpression()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsLike wildCard="*" singleChar="?" escapeChar="!" matchCase="false">
                <fes:ValueReference>name</fes:ValueReference>
                <fes:Literal>A!%B_C*?</fes:Literal>
              </fes:PropertyIsLike>
            </fes:Filter>
            """;

        var result = Fes20Parser.ParseFilter(filterXml);

        var binary = result.Should().BeOfType<BinaryExpression>().Subject;
        binary.Operator.Should().Be(BinaryOperator.Like);

        var left = binary.Left.Should().BeOfType<FunctionCall>().Subject;
        left.FunctionName.Should().Be("LOWER");
        left.Arguments.Should().ContainSingle();
        left.Arguments[0].Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("name");

        var right = binary.Right.Should().BeOfType<Literal>().Subject;
        right.Type.Should().Be(LiteralType.Text);
        right.Value.Should().Be(@"a\%b\_c%_");
    }

    [UnitTest]
    public void ParseFilter_DWithin_ReturnsSpatialDistancePredicate()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:DWithin>
                <fes:ValueReference>geom</fes:ValueReference>
                <fes:Envelope srsName="EPSG:4326">
                  <fes:lowerCorner>-157.9 21.3</fes:lowerCorner>
                  <fes:upperCorner>-157.8 21.4</fes:upperCorner>
                </fes:Envelope>
                <fes:Distance uom="m">25.5</fes:Distance>
              </fes:DWithin>
            </fes:Filter>
            """;

        var result = Fes20Parser.ParseFilter(filterXml);

        var spatial = result.Should().BeOfType<SpatialDistancePredicate>().Subject;
        spatial.Operator.Should().Be(SpatialOperator.DWithin);
        spatial.Left.Should().BeOfType<PropertyReference>().Which.PropertyName.Should().Be("geom");
        spatial.Right.Should().BeOfType<GeometryLiteral>();
        spatial.Distance.Should().BeOfType<Literal>().Which.Value.Should().Be(25.5);
    }

    [UnitTest]
    public void ParseFilter_EmptyLowerBoundary_ThrowsParseException()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsBetween>
                <fes:ValueReference>population</fes:ValueReference>
                <fes:LowerBoundary/>
                <fes:UpperBoundary>
                  <fes:Literal>1000</fes:Literal>
                </fes:UpperBoundary>
              </fes:PropertyIsBetween>
            </fes:Filter>
            """;

        var act = () => Fes20Parser.ParseFilter(filterXml);

        act.Should().Throw<Fes20ParseException>()
            .WithMessage("*LowerBoundary*");
    }

    [UnitTest]
    public void ParseFilter_EmptyUpperBoundary_ThrowsParseException()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsBetween>
                <fes:ValueReference>population</fes:ValueReference>
                <fes:LowerBoundary>
                  <fes:Literal>100</fes:Literal>
                </fes:LowerBoundary>
                <fes:UpperBoundary/>
              </fes:PropertyIsBetween>
            </fes:Filter>
            """;

        var act = () => Fes20Parser.ParseFilter(filterXml);

        act.Should().Throw<Fes20ParseException>()
            .WithMessage("*UpperBoundary*");
    }

    [UnitTest]
    public void ParseFilter_InvalidTypedLiteral_ThrowsParseException()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>count</fes:ValueReference>
                <fes:Literal type="xs:integer">not_a_number</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        var act = () => Fes20Parser.ParseFilter(filterXml);

        act.Should().Throw<Fes20ParseException>()
            .WithMessage("*Cannot parse literal*");
    }

    [UnitTest]
    public void ParseFilter_DateTimeLiteral_PreservesDateTimeOffset()
    {
        const string filterXml = """
            <fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0">
              <fes:PropertyIsEqualTo>
                <fes:ValueReference>timestamp</fes:ValueReference>
                <fes:Literal type="xs:dateTime">2024-02-16T10:00:00-05:00</fes:Literal>
              </fes:PropertyIsEqualTo>
            </fes:Filter>
            """;

        var result = Fes20Parser.ParseFilter(filterXml);

        var binary = result.Should().BeOfType<BinaryExpression>().Subject;
        var literal = binary.Right.Should().BeOfType<Literal>().Subject;
        literal.Type.Should().Be(LiteralType.DateTime);
        literal.Value.Should().BeOfType<DateTimeOffset>()
            .Which.Should().Be(new DateTimeOffset(2024, 2, 16, 10, 0, 0, TimeSpan.FromHours(-5)));
    }
}
