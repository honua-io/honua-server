// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

// Session cookies are Secure whenever the installation is served over HTTPS, including behind a
// TLS-terminating proxy whose forwarded headers are not trusted (the request then looks like HTTP).
public sealed class AdminSessionCookieSecurityTests
{
    [UnitTheory]
    [InlineData("https", "http://honua.internal:8080", true)]
    [InlineData("http", "https://maps.example.com", true)]
    [InlineData("http", "http://honua.internal:8080", false)]
    public void SessionCookie_IsSecure_WhenTheRequestOrThePublicBaseUrlIsHttps(
        string requestScheme, string publicBaseUrl, bool expectedSecure)
    {
        var context = Context(requestScheme, publicBaseUrl);

        AdminAuthEndpoints.UseSecureSessionCookie(context).Should().Be(expectedSecure);
        AdminAuthEndpoints.CreateAdminAuthCookieOptions(context, TimeSpan.FromMinutes(5)).Secure
            .Should().Be(expectedSecure);
    }

    private static DefaultHttpContext Context(string scheme, string publicBaseUrl)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = publicBaseUrl })
            .Build();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<IConfiguration>(configuration).BuildServiceProvider(),
        };
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString("localhost");
        return context;
    }
}
