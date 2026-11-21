// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Xml;
using FluentAssertions;
using Honua.Protocols.Ogc.Classic.Wfs20;
using Honua.Protocols.Ogc.Classic.Wfs20.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Classic.Wfs20;

public sealed class Wfs20CapabilitiesResultTests
{
    [UnitTest]
    public async Task BuildCapabilitiesResult_WritesTheSameResponseAsTheStringWriterDocument()
    {
        var capabilities = CreateCapabilities();
        var expected = await ExecuteAsync(Results.Content(WriteThroughStringWriter(capabilities), "application/xml", Encoding.UTF8));

        var actual = await ExecuteAsync(Wfs20DispatcherEndpoint.BuildCapabilitiesResult(capabilities));

        actual.ContentType.Should().Be(expected.ContentType);
        actual.ContentLength.Should().Be(expected.ContentLength);
        actual.Body.Should().Equal(expected.Body);
        Encoding.UTF8.GetString(actual.Body).Should().StartWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
    }

    // The writer the endpoint used before the pooled buffer: XmlWriter over a StringWriter reporting UTF-8.
    private static string WriteThroughStringWriter(WfsCapabilities capabilities)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            Encoding = Encoding.UTF8,
            OmitXmlDeclaration = false
        };

        using var stringWriter = new Utf8StringWriter();
        using (var writer = XmlWriter.Create(stringWriter, settings))
        {
            Wfs20DispatcherEndpoint.WriteCapabilitiesDocument(writer, capabilities);
        }

        return stringWriter.ToString();
    }

    private static WfsCapabilities CreateCapabilities() => new()
    {
        UpdateSequence = "2026-11-21T00:00:00Z",
        ServiceIdentification = new ServiceIdentification { Title = "Höhe — 東京 & <tag> 😀" },
        ServiceProvider = new ServiceProvider { ProviderName = "Honua" },
        FeatureTypeList = new FeatureTypeList
        {
            FeatureTypes = Enumerable.Range(0, 200)
                .Select(index => new FeatureType
                {
                    Name = $"honua:layer_{index}",
                    Title = $"Layer {index} — Straße 東京",
                    Abstract = index % 2 == 0 ? $"Abstract {index} & \"quoted\"" : null,
                    Keywords = ["roads", "höhe"],
                })
                .ToArray(),
        },
        FilterCapabilities = Wfs20FilterCapabilitiesFactory.Build(),
    };

    private static async Task<(string? ContentType, long? ContentLength, byte[] Body)> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();

        await result.ExecuteAsync(context);

        return (context.Response.ContentType, context.Response.ContentLength, body.ToArray());
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
