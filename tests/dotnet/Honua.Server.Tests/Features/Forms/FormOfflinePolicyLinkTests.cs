// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Forms;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Forms;

[Protocol(TestProtocols.Admin)]
public sealed class FormOfflinePolicyLinkTests
{
    [UnitTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/{formId}/offline-policy")]
    public void OfflinePolicyLinks_UseTheConfiguredPublicBaseUrl_NotTheHostHeader()
    {
        var context = PolicyRequest("https://maps.example.com/gis");

        var link = FormOfflinePolicyService.Absolute(context, "/api/v1/fieldcollection/changes");

        link.Should().Be("https://maps.example.com/gis/api/v1/fieldcollection/changes");
    }

    [UnitTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/{formId}/offline-policy")]
    public void OfflinePolicyLinks_WithoutAPublicBaseUrl_NeverUseTheHostHeader()
    {
        var context = PolicyRequest(publicBaseUrl: null);

        var link = FormOfflinePolicyService.Absolute(context, "/api/v1/fieldcollection/changes");

        link.Should().EndWith("/api/v1/fieldcollection/changes");
        link.Should().NotContain("forged.example.net");
    }

    [UnitTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/{formId}/offline-policy")]
    public void OfflinePolicyLinks_WithoutAPublicBaseUrl_KeepThePathBase()
    {
        var context = PolicyRequest(publicBaseUrl: null);
        context.Request.PathBase = "/gis";

        var link = FormOfflinePolicyService.Absolute(context, "/api/v1/fieldcollection/changes");

        link.Should().Be("https://localhost/gis/api/v1/fieldcollection/changes");
    }

    private static DefaultHttpContext PolicyRequest(string? publicBaseUrl)
    {
        var settings = new Dictionary<string, string?> { ["Public:BaseUrl"] = publicBaseUrl };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider(),
        };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("forged.example.net");
        return context;
    }
}
