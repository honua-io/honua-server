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
    [InlineData("https", "http://honua.internal:8080")]
    [InlineData("http", "https://maps.example.com")]
    public void SessionCookie_IsSecure_WhenTheRequestOrThePublicBaseUrlIsHttps(string requestScheme, string publicBaseUrl)
    {
        var context = Context(requestScheme, publicBaseUrl);

        AdminAuthEndpoints.UseSecureSessionCookie(context).Should().BeTrue();
        AdminAuthEndpoints.CreateAdminAuthCookieOptions(context, TimeSpan.FromMinutes(5)).Secure.Should().BeTrue();
    }

    [UnitTest]
    public void SessionCookie_IsNotSecure_WhenNeitherIsHttps()
    {
        var context = Context("http", "http://honua.internal:8080");

        AdminAuthEndpoints.CreateAdminAuthCookieOptions(context, TimeSpan.FromMinutes(5)).Secure.Should().BeFalse();
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
