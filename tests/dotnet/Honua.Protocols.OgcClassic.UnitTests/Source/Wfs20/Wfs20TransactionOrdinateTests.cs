// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

[Protocol(TestProtocols.Wfs20)]
public sealed class Wfs20TransactionOrdinateTests
{
    [UnitTheory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    public void ParseTransactionGeometry_NonFiniteOrdinateInPos_IsRejected(string ordinate)
    {
        var act = () => Wfs20Handler.ParseTransactionGeometry(XElement.Parse($"""
            <gml:Point srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:pos>{ordinate} 1</gml:pos>
            </gml:Point>
            """), 3857);

        act.Should().Throw<ArgumentException>().WithMessage($"*'{ordinate}'*");
    }

    [UnitTheory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void ParseTransactionGeometry_NonFiniteOrdinateInPosList_IsRejected(string ordinate)
    {
        var act = () => Wfs20Handler.ParseTransactionGeometry(XElement.Parse($"""
            <gml:LineString srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:posList>0 0 {ordinate} 1</gml:posList>
            </gml:LineString>
            """), 3857);

        act.Should().Throw<ArgumentException>().WithMessage($"*'{ordinate}'*");
    }

    [UnitTest]
    public void ParseTransactionGeometry_ExponentOrdinate_IsAccepted()
    {
        var geometry = Wfs20Handler.ParseTransactionGeometry(XElement.Parse("""
            <gml:Point srsName="EPSG:3857" xmlns:gml="http://www.opengis.net/gml/3.2">
              <gml:pos>1.5e3 -2</gml:pos>
            </gml:Point>
            """), 3857);

        geometry.Coordinate.X.Should().Be(1500);
        geometry.Coordinate.Y.Should().Be(-2);
    }
}
