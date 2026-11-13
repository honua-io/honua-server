// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerWmtsCapabilitiesTests
{
    [UnitTest]
    public void Issue5522_GetCapabilities_ResourceUrlPercentEncodesReservedServiceId()
    {
        // Route values arrive decoded, so a service requested as imagery%23west must be
        // re-escaped in both the WMTS base path and the literal layer segment; a raw '#'
        // would turn the rest of the template into a fragment.
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
                .BuildServiceProvider(),
        };
        context.Request.Scheme = "https";
        context.Request.Path = "/rest/services/imagery#west/ImageServer/WMTS/1.0.0/WMTSCapabilities.xml";
        context.Request.QueryString = new QueryString("?token=abc");

        var xml = Honua.Protocols.GeoServices.ImageServer.Handlers.ImageServerWmtsHandler.BuildCapabilitiesXml(
            context,
            "imagery#west",
            timeExtent: null,
            additionalGrids: []);

        xml.Should().Contain(
            "/rest/services/imagery%23west/ImageServer/WMTS/imagery%23west/{Style}/{TileMatrixSet}/{TileMatrix}/{TileRow}/{TileCol}.png?token=abc");
        xml.Should().Contain("<ows:Identifier>imagery#west</ows:Identifier>");
        xml.Should().NotContain("imagery#west/");
    }
}
