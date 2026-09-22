// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

/// <summary>
/// The WFS 1.0.0 Transaction QGIS 3.44.14 actually sends, captured on the wire against
/// the client-compat fixture, must be rewritten into the 2.0 shape the parser understands.
/// Honua answered 501 to this document before the adapter existed, so no stock QGIS
/// session could edit through WFS-T.
/// </summary>
public sealed class Wfs20LegacyTransactionTests
{
    // Verbatim QGIS 3.44.14 Insert (ordinates shortened), including the GML 2 coordinates
    // written longitude,latitude under a latitude-first URN srsName.
    private const string QgisInsert = """
        <Transaction xmlns="http://www.opengis.net/wfs" xmlns:honua="http://honua.io/wfs" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" version="1.0.0" service="WFS" xmlns:gml="http://www.opengis.net/gml"><Insert xmlns="http://www.opengis.net/wfs"><wfs_t_insert_scratch xmlns="http://honua.io/wfs"><name xmlns="http://honua.io/wfs">pyqgis-wfst-authprobe</name><shape xmlns="http://honua.io/wfs"><gml:Point srsName="urn:ogc:def:crs:EPSG::4326"><gml:coordinates ts=" " cs=",">-122.4188,37.7742</gml:coordinates></gml:Point></shape></wfs_t_insert_scratch></Insert></Transaction>
        """;

    private const string QgisDelete = """
        <Transaction xmlns="http://www.opengis.net/wfs" xmlns:ogc="http://www.opengis.net/ogc" version="1.0.0" service="WFS"><Delete xmlns="http://www.opengis.net/wfs" typeName="honua:wfs_t_delete_scratch" xmlns:honua="http://honua.io/wfs"><ogc:Filter><ogc:FeatureId fid="wfs_t_delete_scratch.42"/></ogc:Filter></Delete></Transaction>
        """;

    [Fact]
    public void QgisInsert_IsRewrittenIntoTheTwoZeroShape_WithAxisOrderPreserved()
    {
        var document = XDocument.Parse(QgisInsert);

        var legacyVersion = Wfs20Handler.TryNormaliseLegacyTransaction(document);

        legacyVersion.Should().Be("1.0.0");
        var root = document.Root!;
        root.Name.NamespaceName.Should().Be(Wfs20Utilities.WfsNamespace);
        root.Attribute("version")!.Value.Should().Be(Wfs20Utilities.Version);

        var insert = root.Elements().Single();
        insert.Name.Should().Be(XName.Get("Insert", Wfs20Utilities.WfsNamespace));
        var feature = insert.Elements().Single();
        feature.Name.NamespaceName.Should().Be("http://honua.io/wfs", "application-namespace elements are the client's, not the protocol's");

        var point = feature.Descendants().Single(element => element.Name.LocalName == "Point");
        point.Name.NamespaceName.Should().Be(Wfs20Utilities.GmlNamespace);
        point.Attribute("srsName")!.Value.Should().Be("urn:ogc:def:crs:EPSG::4326", "the client's CRS is kept, only the encoding changes");
        var pos = point.Elements().Single();
        pos.Name.Should().Be(XName.Get("pos", Wfs20Utilities.GmlNamespace), "a GML 3.2 Point carries gml:pos, and the 2.0 parser accepts nothing else for a Point");
        pos.Attributes().Should().BeEmpty("GML 2 cs/ts separators have no GML 3.2 meaning");
        // GML 2 wrote x,y (longitude, latitude); under the URN the 3.2 parser reads latitude
        // first, so the tuple is swapped here for the parser to swap back.
        pos.Value.Should().Be("37.7742 -122.4188");
    }

    [Fact]
    public void QgisDelete_FeatureIdBecomesResourceId()
    {
        var document = XDocument.Parse(QgisDelete);

        Wfs20Handler.TryNormaliseLegacyTransaction(document).Should().Be("1.0.0");

        var filter = document.Root!.Descendants().Single(element => element.Name.LocalName == "Filter");
        filter.Name.NamespaceName.Should().Be(Wfs20Utilities.FesNamespace);
        var resourceId = filter.Elements().Single();
        resourceId.Name.Should().Be(XName.Get("ResourceId", Wfs20Utilities.FesNamespace));
        resourceId.Attribute("fid").Should().BeNull();
        resourceId.Attribute("rid")!.Value.Should().Be("wfs_t_delete_scratch.42");
    }

    [Fact]
    public void TwoZeroDocument_IsLeftAlone()
    {
        var document = XDocument.Parse(
            """<wfs:Transaction xmlns:wfs="http://www.opengis.net/wfs/2.0" service="WFS" version="2.0.0"/>""");

        Wfs20Handler.TryNormaliseLegacyTransaction(document).Should().BeNull();
        document.Root!.Name.NamespaceName.Should().Be(Wfs20Utilities.WfsNamespace);
    }

    // The server reads every geographic CRS latitude-first whatever the srsName spelling
    // (SpatialReferenceHelpers), so the swap must follow the server's reading, not the
    // spelling: a GML 2 x,y tuple is swapped under EPSG:4326 exactly as under the URN.
    [Theory]
    [InlineData("EPSG:4326", "37.7742 -122.4188")]
    [InlineData("urn:ogc:def:crs:EPSG::4326", "37.7742 -122.4188")]
    [InlineData("EPSG:3857", "-13627361.0 4544761.0")]
    public void LegacyCoordinates_FollowTheServersAxisReadingOfTheSrsName(string srsName, string expected)
    {
        var document = XDocument.Parse(
            $"""<Transaction xmlns="http://www.opengis.net/wfs" version="1.0.0" service="WFS" xmlns:gml="http://www.opengis.net/gml"><Insert><f xmlns="urn:x"><g><gml:Point srsName="{srsName}"><gml:coordinates>{(srsName == "EPSG:3857" ? "-13627361.0,4544761.0" : "-122.4188,37.7742")}</gml:coordinates></gml:Point></g></f></Insert></Transaction>""");

        Wfs20Handler.TryNormaliseLegacyTransaction(document);

        document.Root!.Descendants().Single(element => element.Name.LocalName == "pos").Value.Should().Be(expected);
    }
}
