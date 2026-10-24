// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// #6100: the SAML ACS issues <c>honua_admin_session</c> as <c>SameSite=Lax</c>, so the
/// AdminSession scheme (which the Composite default scheme selects whenever that cookie is sent
/// without a bearer) must refuse to authenticate an unsafe request a browser marked as
/// cross-site. Same-origin/same-site browser requests, safe methods, and non-browser clients that
/// send neither <c>Sec-Fetch-Site</c> nor <c>Origin</c> keep authenticating.
/// </summary>
[Protocol(TestProtocols.Admin)]
public sealed class AdminSessionCrossSiteGuardTests
{
    private const string Host = "gis.example.com";

    [UnitTest]
    public async Task UnsafeRequest_WithCrossSiteFetchMetadata_IsNotAuthenticatedBySessionCookie()
    {
        foreach (var method in new[] { "POST", "PUT", "PATCH", "DELETE" })
        {
            var result = await AuthenticateAsync(method, secFetchSite: "cross-site");

            result.Succeeded.Should().BeFalse($"a cross-site {method} must not ride the Lax session cookie");
            result.Failure.Should().NotBeNull();
        }
    }

    [UnitTest]
    public async Task UnsafeRequest_WithForeignOrOpaqueOrigin_AndNoFetchMetadata_IsNotAuthenticated()
    {
        (await AuthenticateAsync("POST", origin: "https://attacker.example.net")).Succeeded.Should().BeFalse();
        (await AuthenticateAsync("POST", origin: "null")).Succeeded.Should().BeFalse();
    }

    [UnitTest]
    public async Task UnsafeRequest_FromSameOriginOrSameSiteOrNonBrowserClient_IsAuthenticated()
    {
        (await AuthenticateAsync("POST", secFetchSite: "same-origin")).Succeeded.Should().BeTrue();
        (await AuthenticateAsync("POST", secFetchSite: "same-site")).Succeeded.Should().BeTrue();
        (await AuthenticateAsync("POST", secFetchSite: "none")).Succeeded.Should().BeTrue();
        (await AuthenticateAsync("POST", origin: $"https://{Host}")).Succeeded.Should().BeTrue();
        (await AuthenticateAsync("POST", origin: $"http://{Host}:8080")).Succeeded.Should().BeTrue();
        (await AuthenticateAsync("POST")).Succeeded.Should().BeTrue("non-browser cookie clients send neither header");
    }

    [UnitTest]
    public async Task SafeRequest_FromCrossSiteNavigation_IsAuthenticated()
    {
        // Lax cookies ride top-level cross-site GETs by design; safe methods must not mutate,
        // which is why calculate is POST-only (#6100).
        (await AuthenticateAsync("GET", secFetchSite: "cross-site")).Succeeded.Should().BeTrue();
    }

    private static async Task<AuthenticateResult> AuthenticateAsync(
        string method,
        string? secFetchSite = null,
        string? origin = null)
    {
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var store = new AdminAuthSessionStore(memoryCache, NullLogger<AdminAuthSessionStore>.Instance);
        var sessionId = await store.CreateAuthenticatedSessionAsync(
            "saml",
            "opaque-token",
            idToken: null,
            [new AdminAuthSessionClaim { Type = ClaimTypes.NameIdentifier, Value = "editor@example.com" }],
            DateTimeOffset.UtcNow.AddMinutes(5),
            CancellationToken.None);

        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new AdminAuthSessionAuthenticationHandler(
            schemes, NullLoggerFactory.Instance, UrlEncoder.Default, store);

        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Host = new HostString(Host);
        context.Request.Headers.Cookie = $"{AdminAuthSessionStore.AuthSessionCookieName}={sessionId}";
        if (secFetchSite is not null)
        {
            context.Request.Headers["Sec-Fetch-Site"] = secFetchSite;
        }

        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        await handler.InitializeAsync(
            new AuthenticationScheme(
                OidcAuthenticationExtensions.AdminSessionScheme, null, typeof(AdminAuthSessionAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }
}
