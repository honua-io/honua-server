// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using FluentAssertions;
using Honua.Protocols.GeoServices.Soap;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.Catalog;

public sealed class SoapRequestXmlTests
{
    [Theory]
    [InlineData("http://schemas.xmlsoap.org/soap/envelope/")]
    [InlineData("http://www.w3.org/2003/05/soap-envelope")]
    public async Task Read_ValidEnvelopeWithArcGisHeaderAndOperation_Accepts(string soapNamespace)
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="{soapNamespace}" xmlns:gis="http://www.esri.com/schemas/ArcGIS/10.8">
              <soap:Header><gis:Security soap:mustUnderstand="1"><gis:Token>example</gis:Token></gis:Security></soap:Header>
              <soap:Body><gis:GetServiceDescriptions /></soap:Body>
            </soap:Envelope>
            """;

        var document = await ReadAsync(xml);

        document.Root!.Name.LocalName.Should().Be("Envelope");
    }

    [Theory]
    [InlineData("<soap:Body><gis:GetVersion /></soap:Body><soap:Header />")]
    [InlineData("<soap:Body><gis:GetVersion /></soap:Body><soap:Body><gis:GetVersion /></soap:Body>")]
    [InlineData("<soap:Body><gis:GetVersion /><gis:GetFields /></soap:Body>")]
    public async Task Read_InvalidEnvelope_Rejects(string body)
    {
        var xml = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/" xmlns:gis="http://www.esri.com/schemas/ArcGIS/10.8">
              {body}
            </soap:Envelope>
            """;
        var action = () => ReadAsync(xml);

        await action.Should().ThrowAsync<XmlSchemaValidationException>();
    }

    [Fact]
    public async Task Read_DocumentAboveLimit_Rejects()
    {
        var action = () => ReadAsync(new string(' ', 257), 256);

        await action.Should().ThrowAsync<XmlException>();
    }

    private static async Task<XDocument> ReadAsync(string xml, long maxCharacters = 1_048_576)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        using var reader = SoapRequestXml.CreateReader(body, maxCharacters);
        return await XDocument.LoadAsync(reader, LoadOptions.None, CancellationToken.None);
    }
}
