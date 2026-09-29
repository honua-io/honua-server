// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;

namespace Honua.Server.Tests.Features.Infrastructure.Errors;

public sealed class LegacyWfsTransactionAdapterTests
{
    private static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";
    private static readonly XNamespace Fes = "http://www.opengis.net/fes/2.0";
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs";
    private static readonly XNamespace Ogc = "http://www.opengis.net/ogc";

    [Fact]
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
        var normalized = Wfs20Handler.NormalizeWfs10Transaction(source);
        normalized.Descendants(Gml + "Point").Single().Attribute("srsName")!.Value.Should().Be("EPSG:4326");
        normalized.Descendants(Gml + "pos").Single().Value.Should().Be("-157.8 21.3");
        normalized.Descendants(Fes + "ResourceId").Single().Attribute("rid")!.Value.Should().Be("places.42");
        normalized.Descendants(XName.Get("Point", "urn:owned")).Single().Value.Should().Be("ordinary field");
        normalized.Descendants(XName.Get("name", "urn:owned")).Single().IsEmpty.Should().BeTrue();
        source.ToString().Should().Be(before);
    }

    [Theory]
    [InlineData("-157.8,21.3,8")]
    [InlineData("-157.8,21.3,8,9")]
    public void AdditionalOrdinates_AreRejectedInsteadOfSilentlyTruncated(string coordinates)
    {
        var source = XElement.Parse($"""
            <Transaction xmlns="http://www.opengis.net/wfs" xmlns:gml="http://www.opengis.net/gml" version="1.0.0">
              <Insert><place xmlns="urn:owned"><geometry><gml:Point><gml:coordinates>{coordinates}</gml:coordinates></gml:Point></geometry></place></Insert>
            </Transaction>
            """);
        var action = () => Wfs20Handler.NormalizeWfs10Transaction(source);
        action.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Wfs10Response_ReportsAssignedIdsAndSuccess()
    {
        var canonical = """
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:fes="http://www.opengis.net/fes/2.0">
              <wfs:TransactionSummary><wfs:totalInserted>1</wfs:totalInserted></wfs:TransactionSummary>
              <wfs:InsertResults><wfs:Feature handle="first"><fes:ResourceId rid="places.43"/></wfs:Feature></wfs:InsertResults>
            </wfs:TransactionResponse>
            """;
        var response = XElement.Parse(Wfs20Handler.FormatWfs10TransactionResponse(canonical));
        response.Name.Should().Be(Wfs + "WFS_TransactionResponse");
        response.Attribute("version")!.Value.Should().Be("1.0.0");
        response.Element(Wfs + "InsertResult")!.Attribute("handle")!.Value.Should().Be("first");
        response.Descendants(Ogc + "FeatureId").Single().Attribute("fid")!.Value.Should().Be("places.43");
        response.Descendants(Wfs + "SUCCESS").Should().ContainSingle();
    }

    [Theory]
    [InlineData("false")]
    [InlineData("unknown")]
    public void PartialOrUnknownCommit_CannotBecomeSuccess(string committed)
    {
        var canonical = $"""
            <wfs:TransactionResponse xmlns:wfs="http://www.opengis.net/wfs/2.0" xmlns:honua="http://honua.io/wfs">
              <honua:OperationResults><honua:OperationResult committed="{committed}"/></honua:OperationResults>
            </wfs:TransactionResponse>
            """;
        var response = XElement.Parse(Wfs20Handler.FormatWfs10TransactionResponse(canonical));
        response.Descendants(Wfs + "SUCCESS").Should().BeEmpty();
        response.Descendants(Wfs + "PARTIAL").Should().ContainSingle();
    }
}
