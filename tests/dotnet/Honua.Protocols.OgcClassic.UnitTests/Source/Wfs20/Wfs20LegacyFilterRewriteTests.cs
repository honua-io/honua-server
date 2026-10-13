// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20LegacyFilterRewriteTests
{
    // One more tuple than the shared filter-geometry vertex limit (50,000).
    private const int TupleCountOverLimit = 50_001;

    [UnitTest]
    public void NormalizeOgcFilterEncoding_OgcFilter_RewritesToFes20()
    {
        const string filter = """
            <ogc:Filter xmlns:ogc="http://www.opengis.net/ogc">
              <ogc:PropertyIsEqualTo><ogc:PropertyName>category</ogc:PropertyName><ogc:Literal>test</ogc:Literal></ogc:PropertyIsEqualTo>
            </ogc:Filter>
            """;

        var normalized = Wfs20Handler.NormalizeOgcFilterEncoding(filter);

        var expression = Fes20Parser.ParseFilter(normalized!);
        expression.Should().BeOfType<BinaryExpression>()
            .Which.Left.Should().BeOfType<PropertyReference>()
            .Which.PropertyName.Should().Be("category");
    }

    [UnitTest]
    public void NormalizeOgcFilterEncoding_Fes20Filter_IsReturnedUnchanged()
    {
        const string filter = """<fes:Filter xmlns:fes="http://www.opengis.net/fes/2.0"><fes:PropertyIsNull><fes:ValueReference>name</fes:ValueReference></fes:PropertyIsNull></fes:Filter>""";

        Wfs20Handler.NormalizeOgcFilterEncoding(filter).Should().BeSameAs(filter);
    }

    [UnitTest]
    public void NormalizeOgcFilterEncoding_CoordinatesOverVertexLimit_IsRejectedBeforeRewrite()
    {
        var coordinates = new StringBuilder(TupleCountOverLimit * 4);
        for (var index = 0; index < TupleCountOverLimit; index++)
        {
            coordinates.Append(index == 0 ? "0,0" : " 0,0");
        }

        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"""<ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml"><ogc:Intersects><ogc:PropertyName>geom</ogc:PropertyName><gml:LineString><gml:coordinates>{coordinates}</gml:coordinates></gml:LineString></ogc:Intersects></ogc:Filter>""");

        var act = () => Wfs20Handler.NormalizeOgcFilterEncoding(filter);

        act.Should().Throw<Fes20ParseException>()
            .Which.ClientReason.Should().Contain("maximum geometry complexity");
    }

    [UnitTest]
    public void NormalizeOgcFilterEncoding_CoordElementsOverVertexLimit_IsRejectedBeforeRewrite()
    {
        var coords = new StringBuilder(TupleCountOverLimit * 40);
        for (var index = 0; index < TupleCountOverLimit; index++)
        {
            coords.Append("<gml:coord><gml:X>0</gml:X><gml:Y>0</gml:Y></gml:coord>");
        }

        var filter =
            $"""<ogc:Filter xmlns:ogc="http://www.opengis.net/ogc" xmlns:gml="http://www.opengis.net/gml"><ogc:Intersects><ogc:PropertyName>geom</ogc:PropertyName><gml:LineString>{coords}</gml:LineString></ogc:Intersects></ogc:Filter>""";

        var act = () => Wfs20Handler.NormalizeOgcFilterEncoding(filter);

        act.Should().Throw<Fes20ParseException>()
            .Which.ClientReason.Should().Contain("maximum geometry complexity");
    }

    [UnitTest]
    public void NormalizeOgcFilterEncoding_NestingBeyondLimit_IsRejected()
    {
        const int depth = FilterExpressionNormalizer.MaxExpressionDepth + 20;
        var filter = """<ogc:Filter xmlns:ogc="http://www.opengis.net/ogc">""" +
            string.Concat(Enumerable.Repeat("<ogc:Not>", depth)) +
            "<ogc:PropertyIsNull><ogc:PropertyName>name</ogc:PropertyName></ogc:PropertyIsNull>" +
            string.Concat(Enumerable.Repeat("</ogc:Not>", depth)) +
            "</ogc:Filter>";

        var act = () => Wfs20Handler.NormalizeOgcFilterEncoding(filter);

        act.Should().Throw<Fes20ParseException>()
            .Which.ClientReason.Should().Contain("maximum XML nesting depth");
    }
}
