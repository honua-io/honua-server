// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Protocols.GeoServices.ImageServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.ImageServer;

[Protocol(TestProtocols.ImageServer)]
public sealed class ImageServerSoapEndpointsTests
{
    [UnitTest]
    public void ResolveImageUrl_RootedPath_ResolvesAgainstRequestBase()
    {
        var context = CreateHttpContext();

        var result = ImageServerSoapEndpoints.ResolveImageUrl(context, "/temp/image.png");

        result.Should().Be("http://localhost/temp/image.png");
    }

    [UnitTest]
    public void ResolveImageUrl_AbsoluteHttpsUrl_PreservesUrl()
    {
        var context = CreateHttpContext();

        var result = ImageServerSoapEndpoints.ResolveImageUrl(context, "https://cdn.example.test/image.png");

        result.Should().Be("https://cdn.example.test/image.png");
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection()
            .AddSingleton<IConfiguration>(new ConfigurationManager())
            .BuildServiceProvider();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost");
        return context;
    }
}
