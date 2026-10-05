// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Queries.Filters;
using Honua.Core.Queries.Filters.Fes20;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

public sealed class LegacyWfsTransactionAdapterTests
{
    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace Fes = "http://www.opengis.net/fes/2.0";
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs";
    private static readonly XNamespace Ogc = "http://www.opengis.net/ogc";

    [UnitTheory]
    [InlineData("Update", "1.0.0", "EPSG:4326", 4326, "-157.8,21.3", -157.8, 21.3)]
    [InlineData("Delete", "1.0.0", "urn:ogc:def:crs:EPSG::4326", 4326, "-157.8,21.3", -157.8, 21.3)]
    [InlineData("Update", "1.0.0", null, 4326, "-157.8,21.3", -157.8, 21.3)]
    [InlineData("Delete", "1.0.0", null, 3857, "1200,3400", 1200, 3400)]
    [InlineData("Update", "1.0.0", "EPSG:3857", 4326, "1200,3400", 1200, 3400)]
    [InlineData("Delete", "1.0.0", "urn:ogc:def:crs:OGC:1.3:CRS84", 4326, "-157.8,21.3", -157.8, 21.3)]
    [InlineData("Update", "1.1.0", "EPSG:4326", 4326, "21.3,-157.8", -157.8, 21.3)]
    public void SpatialEditFilter_ParsesCorrectCoordinatesAfterCanonicalSerialization(
        string action, string version, string? srsName, int defaultSrid, string coordinates, double expectedX, double expectedY)
    {
        var source = XElement.Parse($"""
            <wfs:Transaction xmlns:wfs="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml"
                xmlns:ogc="http://www.opengis.net/ogc" version="{version}">
              <wfs:{action} typeName="places"><ogc:Filter><ogc:Intersects><ogc:PropertyName>geometry</ogc:PropertyName>
                <gml:Point><gml:coordinates>{coordinates}</gml:coordinates></gml:Point>
              </ogc:Intersects></ogc:Filter></wfs:{action}>
            </wfs:Transaction>
            """);
        source.Descendants(XName.Get("Point", "http://www.opengis.net/gml")).Single().SetAttributeValue("srsName", srsName);
        var original = source.ToString();
        var normalized = Wfs20Handler.NormalizeLegacyTransaction(source);
        var filter = normalized.Descendants(Fes + "Filter").Single();
        var canonical = Wfs20Handler.PrepareLegacyTransactionFilter(filter, defaultSrid);
        var expression = Fes20Parser.ParseFilter(canonical.ToString(), defaultSrid)
            .Should().BeOfType<SpatialPredicate>().Subject;
        var literal = expression.Right.Should().BeOfType<GeometryLiteral>().Subject;
        var geometry = new WKBReader().Read(literal.Wkb);

        geometry.Coordinate.X.Should().Be(expectedX);
        geometry.Coordinate.Y.Should().Be(expectedY);
        source.ToString().Should().Be(original);
    }

    [UnitTheory]
    [InlineData("false", "failed-action", "OperationFailed")]
    [InlineData("unknown", null, "CommitOutcomeUnknown")]
    public void Wfs11PartialResponse_ReportsFailuresBeforeInsertResults(string committed, string? handle, string expectedCode)
    {
        var canonical = XElement.Parse($"""
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:fes="http://www.opengis.net/fes/2.0"
                xmlns:honua="http://honua.io/wfs">
              <wfs:TransactionSummary><wfs:totalInserted>1</wfs:totalInserted></wfs:TransactionSummary>
              <wfs:InsertResults><wfs:Feature><fes:ResourceId rid="places.43"/></wfs:Feature></wfs:InsertResults>
              <honua:OperationResults>
                <honua:OperationResult sequence="0" committed="true"/>
                <honua:OperationResult sequence="1" committed="{committed}"><honua:Error>Edit failed.</honua:Error></honua:OperationResult>
              </honua:OperationResults>
            </wfs:TransactionResponse>
            """);
        canonical.Descendants(XName.Get("OperationResult", "http://honua.io/wfs")).Last().SetAttributeValue("handle", handle);

        var response = XElement.Parse(Wfs20Handler.FormatLegacyTransactionResponse(canonical.ToString(), "1.1.0"));

        response.Elements().Select(element => element.Name).Should()
            .Equal(Wfs + "TransactionSummary", Wfs + "TransactionResults", Wfs + "InsertResults");
        var failure = response.Descendants(Wfs + "Action").Should().ContainSingle().Subject;
        failure.Attribute("locator")!.Value.Should().Be(handle ?? "operation-1");
        failure.Attribute("code")!.Value.Should().Be(expectedCode);
        failure.Element(Wfs + "Message")!.Value.Should().Be("Edit failed.");
        response.Descendants(XName.Get("OperationResults", "http://honua.io/wfs")).Should().BeEmpty();
        response.Descendants(Ogc + "FeatureId").Single().Attribute("fid")!.Value.Should().Be("places.43");
    }

    [UnitTheory]
    [InlineData("LineString")]
    [InlineData("Box")]
    public void Wfs10SpatialFilter_PreservesCoordinateListsAndEnvelopeBounds(string geometryType)
    {
        var source = XElement.Parse($"""
            <wfs:Transaction xmlns:wfs="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml"
                xmlns:ogc="http://www.opengis.net/ogc" version="1.0.0">
              <wfs:Delete typeName="places"><ogc:Filter><ogc:Intersects><ogc:PropertyName>geometry</ogc:PropertyName>
                <gml:{geometryType} srsName="EPSG:4326"><gml:coordinates>-158,21 -157,22</gml:coordinates></gml:{geometryType}>
              </ogc:Intersects></ogc:Filter></wfs:Delete>
            </wfs:Transaction>
            """);
        var normalized = Wfs20Handler.NormalizeLegacyTransaction(source);
        var filter = Wfs20Handler.PrepareLegacyTransactionFilter(normalized.Descendants(Fes + "Filter").Single(), 4326);
        var predicate = Fes20Parser.ParseFilter(filter.ToString(), 4326).Should().BeOfType<SpatialPredicate>().Subject;
        var literal = predicate.Right.Should().BeOfType<GeometryLiteral>().Subject;
        var bounds = new WKBReader().Read(literal.Wkb).EnvelopeInternal;

        bounds.MinX.Should().Be(-158);
        bounds.MaxX.Should().Be(-157);
        bounds.MinY.Should().Be(21);
        bounds.MaxY.Should().Be(22);
    }

    [UnitTest]
    public void NativeWfs10Update_PreservesFeatureIdPropertiesAndCoordinateOrder()
    {
        var source = XElement.Parse("""
            <wfs:Transaction xmlns:wfs="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml"
                xmlns:ogc="http://www.opengis.net/ogc" xmlns:app="urn:owned" version="1.0.0">
              <wfs:Update typeName="app:places"><wfs:Property><wfs:Name>app:geometry</wfs:Name><wfs:Value>
                <gml:Point srsName="urn:ogc:def:crs:EPSG::4326"><gml:coordinates>-157.8,21.3</gml:coordinates></gml:Point>
              </wfs:Value></wfs:Property><ogc:Filter><ogc:FeatureId fid="places.42"/></ogc:Filter></wfs:Update>
              <wfs:Insert><app:places><app:Point>ordinary field</app:Point><app:name/></app:places></wfs:Insert>
            </wfs:Transaction>
            """);
        var before = source.ToString();
        var normalized = Wfs20Handler.NormalizeLegacyTransaction(source);
        normalized.Descendants(Gml + "Point").Single().Attribute("srsName")!.Value.Should().Be("urn:ogc:def:crs:EPSG::4326");
        normalized.Descendants(Gml + "pos").Single().Value.Should().Be("-157.8 21.3");
        normalized.Descendants(Fes + "ResourceId").Single().Attribute("rid")!.Value.Should().Be("places.42");
        normalized.Descendants(XName.Get("Point", "urn:owned")).Single().Value.Should().Be("ordinary field");
        normalized.Descendants(XName.Get("name", "urn:owned")).Single().IsEmpty.Should().BeTrue();
        var geometry = Wfs20Handler.ParseTransactionGeometry(normalized.Descendants(Gml + "Point").Single(), 4326);
        geometry.Coordinate.X.Should().Be(-157.8);
        geometry.Coordinate.Y.Should().Be(21.3);
        source.ToString().Should().Be(before);
    }

    [UnitTheory]
    [InlineData("-157.8,21.3,8")]
    [InlineData("-157.8,21.3,8,9")]
    public void AdditionalOrdinates_AreRejectedInsteadOfSilentlyTruncated(string coordinates)
    {
        var source = XElement.Parse($"""
            <Transaction xmlns="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml" version="1.0.0">
              <Insert><place xmlns="urn:owned"><geometry><gml:Point><gml:coordinates>{coordinates}</gml:coordinates></gml:Point></geometry></place></Insert>
            </Transaction>
            """);
        var action = () => Wfs20Handler.NormalizeLegacyTransaction(source);
        action.Should().Throw<NotSupportedException>();
    }

    [UnitTest]
    public void Wfs10Response_ReportsAssignedIdsAndSuccess()
    {
        var canonical = """
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:TransactionSummary><wfs:totalInserted>1</wfs:totalInserted></wfs:TransactionSummary>
              <wfs:InsertResults><wfs:Feature handle="first"><fes:ResourceId rid="places.43"/></wfs:Feature></wfs:InsertResults>
            </wfs:TransactionResponse>
            """;
        var response = XElement.Parse(Wfs20Handler.FormatLegacyTransactionResponse(canonical, "1.0.0"));
        response.Name.Should().Be(Wfs + "WFS_TransactionResponse");
        response.Attribute("version")!.Value.Should().Be("1.0.0");
        response.Element(Wfs + "InsertResult")!.Attribute("handle")!.Value.Should().Be("first");
        response.Descendants(Ogc + "FeatureId").Single().Attribute("fid")!.Value.Should().Be("places.43");
        response.Descendants(Wfs + "SUCCESS").Should().ContainSingle();
    }

    [UnitTest]
    public void Wfs11Geometry_KeepsGeographicAxisOrder()
    {
        var root = XElement.Parse("""
            <Transaction xmlns="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml" version="1.1.0">
              <Insert><place xmlns="urn:owned"><geometry><gml:Point srsName="urn:ogc:def:crs:EPSG::4326">
                <gml:pos srsDimension="2">21.3 -157.8</gml:pos>
              </gml:Point></geometry></place></Insert>
            </Transaction>
            """);
        var normalized = Wfs20Handler.NormalizeLegacyTransaction(root);
        var geometry = Wfs20Handler.ParseTransactionGeometry(normalized.Descendants(Gml + "Point").Single(), 4326);
        geometry.Coordinate.X.Should().Be(-157.8);
        geometry.Coordinate.Y.Should().Be(21.3);
    }

    [UnitTest]
    public void Wfs11Response_UsesLegacyNamespacesAndCompleteOrderedSummary()
    {
        var canonical = """
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:TransactionSummary><wfs:totalInserted>1</wfs:totalInserted></wfs:TransactionSummary>
              <wfs:InsertResults><wfs:Feature><fes:ResourceId rid="places.43"/></wfs:Feature></wfs:InsertResults>
            </wfs:TransactionResponse>
            """;
        var response = XElement.Parse(Wfs20Handler.FormatLegacyTransactionResponse(canonical, "1.1.0"));
        response.Name.Should().Be(Wfs + "TransactionResponse");
        response.Attribute("version")!.Value.Should().Be("1.1.0");
        response.Element(Wfs + "TransactionSummary")!.Elements().Select(element => element.Name.LocalName)
            .Should().Equal("totalInserted", "totalUpdated", "totalDeleted");
        response.Element(Wfs + "TransactionSummary")!.Elements().Select(element => element.Value)
            .Should().Equal("1", "0", "0");
        response.Descendants(Ogc + "FeatureId").Single().Attribute("fid")!.Value.Should().Be("places.43");
    }

    [UnitTheory]
    [InlineData("false")]
    [InlineData("unknown")]
    public void PartialOrUnknownCommit_CannotBecomeSuccess(string committed)
    {
        var canonical = $"""
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:honua="http://honua.io/wfs">
              <honua:OperationResults><honua:OperationResult committed="{committed}"/></honua:OperationResults>
            </wfs:TransactionResponse>
            """;
        var response = XElement.Parse(Wfs20Handler.FormatLegacyTransactionResponse(canonical, "1.0.0"));
        response.Descendants(Wfs + "SUCCESS").Should().BeEmpty();
        response.Descendants(Wfs + "PARTIAL").Should().ContainSingle();
    }
}
