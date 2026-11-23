// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /admin")]
    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task SignedOutLanding_ForTheEmittedPostLogoutUri_RedirectsToConsoleSignIn()
    {
        var host = await _baseHost.GetPublicBaseUrlGenericOidcHostAsync(callback: null);
        using var browser = host.Fixture.CreateClient(allowAutoRedirect: false);
        using var logout = await browser.GetAsync("/api/v1/admin/auth/providers/oidc/logout-url");
        var content = await logout.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthLogoutUrlResponse);
        var landing = QueryHelpers.ParseQuery(new Uri(content!.LogoutUrl!).Query)["post_logout_redirect_uri"].ToString();
        landing.Should().Be("https://public.example.com/honua/admin");

        foreach (var path in new[] { "/admin", "/admin/" })
        {
            using var response = await browser.GetAsync(path);

            response.StatusCode.Should().Be(HttpStatusCode.Redirect, path);
            response.Headers.Location!.OriginalString.Should().Be("/console/auth/signin", path);
            response.Headers.CacheControl!.NoStore.Should().BeTrue(path);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /admin")]
    public async Task SignedOutLanding_WithSeparateConsoleCallback_RedirectsToThatOriginsSignIn()
    {
        var host = await _baseHost.GetPublicBaseUrlGenericOidcHostAsync("https://console.example.com/admin/auth/callback");
        using var browser = host.Fixture.CreateClient(allowAutoRedirect: false);

        using var response = await browser.GetAsync("/admin");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location.Should().Be(new Uri("https://console.example.com/console/auth/signin"));
    }
}
