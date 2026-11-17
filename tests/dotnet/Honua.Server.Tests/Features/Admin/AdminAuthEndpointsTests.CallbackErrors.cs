// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [IntegrationTest]
    [Endpoint("GET /admin/auth/callback")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_ProviderError_RendersReadablePageAndEndsAttempt()
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = CreateHttpsBrowser(fixture);
        var state = await StartBrowserSignInAsync(browser);

        using var callback = await browser.GetAsync(
            "/admin/auth/callback?error=invalid_scope&error_description=Call+0800+support&state=" + state);

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        callback.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        callback.Headers.CacheControl!.NoStore.Should().BeTrue();
        var page = await callback.Content.ReadAsStringAsync();
        page.Should().Contain("Sign-in failed").And.Contain("<code>invalid_scope</code>")
            .And.Contain("href=\"/console/auth/signin\"").And.NotContain("0800");
        callback.Headers.GetValues("Set-Cookie").Should().Contain(value => value.StartsWith("honua_admin_login=;", StringComparison.Ordinal));
        using var replay = await browser.GetAsync("/admin/auth/callback?code=code-123&state=" + state);
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await IsAuthenticatedAsync(browser)).Should().BeFalse();
    }

    [IntegrationTest]
    [Endpoint("GET /admin/auth/callback")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_ProviderErrorThatIsNotAToken_ShowsNeutralCode()
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = CreateHttpsBrowser(fixture);
        var state = await StartBrowserSignInAsync(browser);

        using var callback = await browser.GetAsync(
            "/admin/auth/callback?error=call+support+now!&state=" + state);

        callback.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var page = await callback.Content.ReadAsStringAsync();
        page.Should().Contain("<code>unrecognized_error</code>").And.NotContain("support now");
    }

    [IntegrationTest]
    [Endpoint("GET /admin/auth/callback")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/authorize-url")]
    public async Task BrowserCallback_ForgedProviderError_DoesNotCancelSignIn()
    {
        using var stubFactory = CreateCallbackStub();
        await using var fixture = CreateGenericOidcFixture(stubFactory);
        await fixture.InitializeAsync();
        using var browser = CreateHttpsBrowser(fixture);
        var state = await StartBrowserSignInAsync(browser);

        using var forged = await browser.GetAsync("/admin/auth/callback?error=access_denied&state=forged");
        forged.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        forged.Headers.Contains("Set-Cookie").Should().BeFalse();

        using var callback = await browser.GetAsync("/admin/auth/callback?code=code-123&state=" + state);
        callback.StatusCode.Should().Be(HttpStatusCode.Redirect);
        (await IsAuthenticatedAsync(browser)).Should().BeTrue();
    }


    private static HttpClient CreateHttpsBrowser(WebAppFixture fixture)
    {
        var browser = fixture.CreateClient(allowAutoRedirect: false);
        browser.BaseAddress = new Uri("https://localhost");
        return browser;
    }

    private static async Task<string> StartBrowserSignInAsync(HttpClient browser)
    {
        using var authorize = await browser.PostAsJsonAsync("/api/v1/admin/auth/providers/oidc/authorize-url", new { });
        authorize.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await authorize.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
        return QueryHelpers.ParseQuery(new Uri(body!.AuthorizeUrl).Query)["state"].ToString();
    }

    private static async Task<bool> IsAuthenticatedAsync(HttpClient browser)
    {
        using var identity = await browser.GetAsync("/api/v1/admin/auth/session");
        var session = await identity.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
        return session!.IsAuthenticated;
    }
}
