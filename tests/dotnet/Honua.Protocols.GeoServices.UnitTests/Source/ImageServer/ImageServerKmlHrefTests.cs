// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer.Handlers;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerKmlHrefTests
{
    private static readonly ImageServerKmlHandler.GeographicBox Box = new(0, 0, 4, 2);

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/kml/image.kmz")]
    public void ExportImageHref_UsesTheConfiguredPublicBaseUrl_NotTheHostHeader()
    {
        var context = KmlRequest("https://maps.example.com");

        var href = ImageServerKmlHandler.BuildExportImageHref(context, Box);

        href.Should().StartWith("https://maps.example.com/rest/services/parcels/ImageServer/exportImage?");
        href.Should().NotContain("forged.example.net");
    }

    [UnitTest]
    [Operation(Operations.GetServiceInfo)]
    [Endpoint("GET /rest/services/{serviceId}/ImageServer/kml/image.kmz")]
    public void ExportImageHref_WithoutAPublicBaseUrl_NeverUsesTheHostHeader()
    {
        var context = KmlRequest(publicBaseUrl: null);

        var href = ImageServerKmlHandler.BuildExportImageHref(context, Box);

        href.Should().Contain("/rest/services/parcels/ImageServer/exportImage?");
        href.Should().NotContain("forged.example.net");
    }

    private static DefaultHttpContext KmlRequest(string? publicBaseUrl)
    {
        var settings = new Dictionary<string, string?> { ["Public:BaseUrl"] = publicBaseUrl };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider(),
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("forged.example.net");
        context.Request.Path = "/rest/services/parcels/ImageServer/kml/image.kmz";
        return context;
    }
}
