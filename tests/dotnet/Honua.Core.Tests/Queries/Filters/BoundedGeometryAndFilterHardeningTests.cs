// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers.Binary;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Validation;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.TestKit.Attributes;
using Moq;

namespace Honua.Core.Tests.Queries.Filters;

public sealed class BoundedGeometryAndFilterHardeningTests
{
    private readonly FilterExpressionService _service = new(Mock.Of<IFilterExpressionTranslator>());

    [UnitTest]
    public void EnsureWktNestingDepth_200LevelGeometryCollection_ThrowsArgumentException()
    {
        var nestedWkt = BuildNestedGeometryCollectionWkt(200);

        var act = () => BoundedGeometryNestingGuard.EnsureWktNestingDepth(nestedWkt, "WKT literal");

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {BoundedGeometryNestingGuard.MaxWktNestingDepth}*");
    }

    [UnitTest]
    public void Parse_Cql2With200LevelNestedGeometryCollectionWkt_ReturnsFailureWithoutStackOverflow()
    {
        var nestedWkt = BuildNestedGeometryCollectionWkt(200);

        var result = _service.Parse(FilterLanguage.Cql2Text, $"S_INTERSECTS(geom, {nestedWkt})");

        result.IsSuccess.Should().BeFalse();
        result.Expression.Should().BeNull();
    }

    [UnitTest]
    public void Parse_ODataWith200LevelNestedGeometryCollectionWkt_ReturnsFailureWithoutStackOverflow()
    {
        var nestedWkt = BuildNestedGeometryCollectionWkt(200);

        var result = _service.Parse(FilterLanguage.OData, $"geo.intersects(geom, geography'{nestedWkt}')");

        result.IsSuccess.Should().BeFalse();
        result.Expression.Should().BeNull();
    }

    [UnitTest]
    public void EnsureWkbNestingDepth_200LevelGeometryCollection_ThrowsArgumentException()
    {
        var nestedWkb = BuildNestedGeometryCollectionWkb(200);

        var act = () => BoundedGeometryNestingGuard.EnsureWkbNestingDepth(nestedWkb, "WKB geometry");

        act.Should().Throw<ArgumentException>()
            .WithMessage($"*maximum nesting depth of {BoundedGeometryNestingGuard.MaxWkbNestingDepth}*");
    }

    [UnitTest]
    public void Parse_Cql2WithDeeplyNestedArrayLiterals_ReturnsFailure()
    {
        var depth = FilterParserGuard.MaxExpressionDepth + 20;
        var deepArray = string.Concat(Enumerable.Repeat("(", depth)) +
                        "1, 2" +
                        string.Concat(Enumerable.Repeat(")", depth));
        var filter = $"A_EQUALS(tags, {deepArray})";

        var result = _service.Parse(FilterLanguage.Cql2Text, filter);

        result.IsSuccess.Should().BeFalse();
        result.Expression.Should().BeNull();
    }

    [UnitTest]
    public void Parse_Cql2WithDeeplyNestedCaseiPattern_ReturnsFailure()
    {
        var depth = FilterParserGuard.MaxExpressionDepth + 10;
        var pattern = string.Concat(Enumerable.Repeat("CASEI(", depth)) +
                      "'abc'" +
                      string.Concat(Enumerable.Repeat(")", depth));

        var result = _service.Parse(FilterLanguage.Cql2Text, $"name LIKE {pattern}");

        result.IsSuccess.Should().BeFalse();
        result.Expression.Should().BeNull();
    }

    [UnitTest]
    public void Fes20ParseFilter_XmlExceedingMaxElementDepth_ThrowsFes20ParseException()
    {
        var depth = RemoteXmlDocumentParser.DefaultMaxElementDepth + 20;
        var xml = "<fes:Filter xmlns:fes=\"http://www.opengis.net/fes/2.0\" xmlns:gml=\"http://www.opengis.net/gml/3.2\">" +
                  string.Concat(Enumerable.Repeat("<gml:a>", depth)) +
                  "x" +
                  string.Concat(Enumerable.Repeat("</gml:a>", depth)) +
                  "</fes:Filter>";

        var act = () => Fes20Parser.ParseFilter(xml);

        act.Should().Throw<Fes20ParseException>();
    }

    [UnitTest]
    public void Parse_ODataNestedReplaceAmplificationChain_ReturnsFailure()
    {
        var filter = "replace(replace(replace(replace(replace('aaaaaaaaaa', 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa') eq 'x'";

        var result = _service.Parse(FilterLanguage.OData, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("derived string exceeds the maximum length");
    }

    [UnitTest]
    public void Parse_GeoServicesSqlNestedReplaceOnPropertyAmplificationChain_ReturnsFailure()
    {
        var filter = "REPLACE(REPLACE(REPLACE(name, 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa'), 'a', 'aaaaaaaaaa') = 'x'";

        var result = _service.Parse(FilterLanguage.GeoServicesSql, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("derived string exceeds the maximum length");
    }

    [UnitTest]
    public void Parse_ODataNestedConcatAmplificationChain_ReturnsFailure()
    {
        var chunk = new string('a', 1_024);
        var inner = $"concat('{chunk}', '{chunk}', '{chunk}', '{chunk}', '{chunk}', '{chunk}', '{chunk}', '{chunk}')";
        var filter = $"concat({inner}, {inner}, {inner}, {inner}, {inner}, {inner}, {inner}, {inner}, {inner}) eq 'x'";

        var result = _service.Parse(FilterLanguage.OData, filter);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("derived string exceeds the maximum length");
    }

    [UnitTest]
    public void Parse_ExcessiveReplaceNestingDepth_ReturnsFailure()
    {
        var expr = "'abc'";
        for (var i = 0; i < FilterStringAmplificationGuard.MaxStringReplacementDepth + 1; i++)
        {
            expr = $"replace({expr}, 'a', 'b')";
        }

        var result = _service.Parse(FilterLanguage.OData, $"{expr} eq 'x'");

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("maximum string replacement nesting depth");
    }

    [UnitTest]
    public void Parse_LegitimateReplaceAndConcat_Succeeds()
    {
        var odata = _service.Parse(FilterLanguage.OData, "concat(replace(Name, 'St', 'Street'), ' City') eq 'Main Street City'");
        var sql = _service.Parse(FilterLanguage.GeoServicesSql, "CONCAT(REPLACE(name, 'St', 'Street'), ' City') = 'Main Street City'");

        odata.IsSuccess.Should().BeTrue(odata.ErrorMessage);
        sql.IsSuccess.Should().BeTrue(sql.ErrorMessage);
    }

    [UnitTest]
    public void Parse_FilterTextExceedingMaxFilterLength_ReturnsFailure()
    {
        var oversized = new string(' ', FilterParserGuard.MaxFilterTextLength + 1) + "a = 1";

        var result = _service.Parse(FilterLanguage.GeoServicesSql, oversized);

        result.IsSuccess.Should().BeFalse();
        result.ErrorMessage.Should().Contain("exceeds the maximum length");
    }

    private static string BuildNestedGeometryCollectionWkt(int depth)
        => string.Concat(Enumerable.Repeat("GEOMETRYCOLLECTION(", depth)) +
           "POINT(0 0)" +
           new string(')', depth);

    private static byte[] BuildNestedGeometryCollectionWkb(int depth)
    {
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[9];
        for (var i = 0; i < depth; i++)
        {
            header[0] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(header[1..5], 7u);
            BinaryPrimitives.WriteUInt32LittleEndian(header[5..9], 1u);
            stream.Write(header);
        }

        Span<byte> point = stackalloc byte[21];
        point[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(point[1..5], 1u);
        BinaryPrimitives.WriteDoubleLittleEndian(point[5..13], 0.0);
        BinaryPrimitives.WriteDoubleLittleEndian(point[13..21], 0.0);
        stream.Write(point);
        return stream.ToArray();
    }
}
