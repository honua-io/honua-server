// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Xml;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20.Services;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class Wfs20GmlDocumentResultTests
{
    private const string GmlMediaType = "application/gml+xml; version=3.2";

    [UnitTheory]
    [InlineData("name")]
    [InlineData("Name_2")]
    [InlineData("_private")]
    [InlineData("a-b.c")]
    [InlineData("_x0020_")]
    [InlineData("a_X12")]
    [InlineData("field_x")]
    [InlineData("1st")]
    [InlineData("-dash")]
    [InlineData("with space")]
    [InlineData("na:me")]
    [InlineData("höhe")]
    [InlineData("東京")]
    [InlineData("")]
    public void EncodeFieldLocalName_MatchesXmlConvert(string name)
    {
        Wfs20Handler.EncodeFieldLocalName(name).Should().Be(XmlConvert.EncodeLocalName(name));
    }

    [UnitTest]
    public async Task WriteGmlDocumentResult_WritesTheSameResponseAsTheStringDocument()
    {
        var expected = await ExecuteAsync(Results.Content(Wfs20Handler.WriteXmlDocument(WriteCollection), GmlMediaType, Encoding.UTF8));

        var actual = await ExecuteAsync(Wfs20Handler.WriteGmlDocumentResult(WriteCollection));

        actual.ContentType.Should().Be(expected.ContentType);
        actual.ContentLength.Should().Be(expected.ContentLength);
        actual.Body.Should().Equal(expected.Body);
    }

    private static void WriteCollection(XmlWriter writer)
    {
        writer.WriteStartDocument();
        writer.WriteStartElement("wfs", "FeatureCollection", "http://www.opengis.net/wfs/2.0");
        writer.WriteAttributeString("xmlns", "gml", null, "http://www.opengis.net/gml/3.2");
        writer.WriteAttributeString("numberReturned", "500");
        for (var index = 0; index < 500; index++)
        {
            writer.WriteStartElement("wfs", "member", "http://www.opengis.net/wfs/2.0");
            writer.WriteStartElement("honua", "layer", "urn:honua");
            writer.WriteAttributeString("gml", "id", "http://www.opengis.net/gml/3.2", $"layer.{index}");
            writer.WriteElementString("honua", "name", "urn:honua", $"Höhe {index} — 東京 & <tag>");
            writer.WriteRaw("<gml:Point><gml:pos>37.5 -122.5</gml:pos></gml:Point>");
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndDocument();
    }

    private static async Task<(string? ContentType, long? ContentLength, byte[] Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await result.ExecuteAsync(context);

        return (context.Response.ContentType, context.Response.ContentLength, body.ToArray());
    }
}
