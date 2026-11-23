// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed partial class Wfs20EndpointsTests
{
    [IntegrationTheory]
    [InlineData("1.0.0", "name", 0)]
    [InlineData("1.0.0", "name,geometry", 1)]
    [InlineData("1.0.0", "*", 1)]
    [InlineData("1.0.0", null, 1)]
    [InlineData("1.1.0", "name", 0)]
    [InlineData("1.1.0", "name,honua:geometry", 1)]
    [InlineData("1.1.0", "*", 1)]
    [InlineData("1.1.0", null, 1)]
    [InlineData("2.0.0", "name", 0)]
    [InlineData("2.0.0", "name,honua:geometry", 1)]
    [InlineData("2.0.0", "*", 1)]
    [InlineData("2.0.0", null, 1)]
    [Protocol(TestProtocols.Wfs10)]
    [Protocol(TestProtocols.Wfs11)]
    [Endpoint("GET /wfs")]
    [InterfaceOperation(TestProtocols.Wfs10, "GetFeature")]
    [InterfaceOperation(TestProtocols.Wfs11, "GetFeature")]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_GetFeature_PropertyProjection_ControlsOptionalGeometry(
        string version, string? propertyName, int geometryCount)
    {
        using var response = await _fixture.Client.GetAsync(
            $"/wfs?SERVICE=WFS&REQUEST=GetFeature&VERSION={version}&TYPENAME=pub-layer-0&MAXFEATURES=1" +
            (propertyName is null ? string.Empty : $"&PROPERTYNAME={Uri.EscapeDataString(propertyName)}"));
        await AssertProjectedGeometryAsync(response, geometryCount);
    }

    [IntegrationTheory]
    [InlineData("name", 0)]
    [InlineData("name,honua:shape", 1)]
    [InlineData("*", 1)]
    [InlineData(null, 1)]
    [Endpoint("POST /wfs")]
    [Operation(Operations.Query)]
    [InterfaceOperation(TestProtocols.Wfs20, "GetFeature")]
    public async Task Wfs_PostGetFeature_PropertyProjection_ControlsOptionalGeometry(
        string? propertyName, int geometryCount)
    {
        XNamespace wfs = "http://www.opengis.net/wfs/2.0";
        var query = new XElement(wfs + "Query", new XAttribute("typeNames", "honua:pub-layer-0"));
        if (propertyName is not null)
        {
            query.Add(propertyName.Split(',').Select(name => new XElement(wfs + "PropertyName", name)));
        }

        var request = new XElement(wfs + "GetFeature",
            new XAttribute("service", "WFS"), new XAttribute("version", "2.0.0"),
            new XAttribute("count", "1"), new XAttribute(XNamespace.Xmlns + "honua", "http://honua.io/wfs"),
            query);
        using var requestContent = new StringContent(request.ToString(), Encoding.UTF8, "application/xml");
        using var response = await _fixture.Client.PostAsync("/wfs", requestContent);
        await AssertProjectedGeometryAsync(response, geometryCount);
    }

    private static async Task AssertProjectedGeometryAsync(HttpResponseMessage response, int geometryCount)
    {
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, content);
        var document = XDocument.Parse(content);
        document.Descendants(XName.Get("name", "http://honua.io/wfs")).Should().ContainSingle();
        document.Descendants(XName.Get("shape", "http://honua.io/wfs"))
            .Should().HaveCount(geometryCount);
    }
}
