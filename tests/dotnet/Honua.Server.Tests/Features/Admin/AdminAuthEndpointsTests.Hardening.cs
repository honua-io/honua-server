// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class AdminAuthEndpointsTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithExpiredIdTokenWithinClockSkew_IsRejectedWithoutCreatingSession()
    {
        await AssertRejectedTokenExchangeAsync((stub, nonce) =>
        {
            stub.IdTokenOverride = CreateIdToken(new IdTokenSpec
            {
                Name = "Expired Admin",
                Nonce = nonce,
                Expires = DateTimeOffset.UtcNow.AddSeconds(-30),
            });
        });
    }

    [IntegrationTheory]
    [InlineData(null)]
    [InlineData("wrong-nonce")]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithMissingOrMismatchedNonce_IsRejected(string? tokenNonce)
    {
        await AssertRejectedTokenExchangeAsync((stub, _) =>
        {
            stub.IdTokenOverride = CreateIdToken(new IdTokenSpec
            {
                Name = "Nonce Mismatch Admin",
                Nonce = tokenNonce,
            });
        });
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/auth/providers/{providerKey}/token")]
    public async Task RequestToken_WithUntrustedDiscoveryIssuer_IsRejected()
    {
        await AssertRejectedTokenExchangeAsync((stub, nonce) =>
        {
            stub.IssuerOverride = "https://untrusted-issuer.example";
            stub.NonceOverride = nonce;
        });
    }

    [UnitTest]
    public void IsAllowedDiscoveryIssuer_MatchesConfiguredAuthorityAndValidIssuersOnly()
    {
        var oidcOptions = new OidcAuthenticationOptions
        {
            Enabled = true,
            Generic = new GenericOidcProviderOptions
            {
                Enabled = true,
                Authority = "https://auth.example.com",
                ClientId = "generic-client-id",
            },
            TokenValidation = new TokenValidationOptions
            {
                ValidIssuers = ["https://alias.example.com"],
            },
        };
        OidcProviderCatalog.TryResolveProvider(oidcOptions, "oidc", out var provider).Should().BeTrue();

        AdminAuthEndpoints.IsAllowedDiscoveryIssuer("https://auth.example.com", provider, oidcOptions).Should().BeTrue();
        AdminAuthEndpoints.IsAllowedDiscoveryIssuer("https://auth.example.com/", provider, oidcOptions).Should().BeTrue();
        AdminAuthEndpoints.IsAllowedDiscoveryIssuer("https://alias.example.com", provider, oidcOptions).Should().BeFalse();
        AdminAuthEndpoints.IsAllowedDiscoveryIssuer("https://evil.example.com", provider, oidcOptions).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("javascript:alert(1)")]
    [InlineData("http://login.example.com/logout")]
    [Endpoint("GET /api/v1/admin/auth/providers/{providerKey}/logout-url")]
    public async Task GetLogoutUrl_WithUnsafeDiscoveryLogoutEndpoint_ReturnsNullLogoutUrl(string unsafeEndpoint)
    {
        using var stubFactory = new StubHttpClientFactory(
            "https://auth.example.com/.well-known/openid-configuration",
            new StubOidcEndpoints(
                "https://auth.example.com/authorize",
                "https://auth.example.com/token",
                unsafeEndpoint));
        var oidcFixture = CreateGenericOidcFixture(stubFactory)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("Public:BaseUrl", "https://public.example.com/honua");
            });
        try
        {
            await oidcFixture.InitializeAsync();
            var response = await oidcFixture.CreateClient().GetAsync(
                "/api/v1/admin/auth/providers/oidc/logout-url");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthLogoutUrlResponse);
            content.Should().NotBeNull();
            content!.LogoutUrl.Should().BeNull();
        }
        finally
        {
            await oidcFixture.DisposeAsync();
        }
    }

    private async Task AssertRejectedTokenExchangeAsync(Action<StubHttpClientFactory, string> configureStub)
    {
        var host = await _baseHost.GetGenericOidcHostAsync();
        var stubFactory = host.Stub!;

        try
        {
            using var oidcClient = host.CreateBrowser();
            var authorizeResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/authorize-url",
                new { });
            var authorizeContent = await authorizeResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthAuthorizeUrlResponse);
            var authorizeParameters = QueryHelpers.ParseQuery(new Uri(authorizeContent!.AuthorizeUrl).Query);
            configureStub(stubFactory, authorizeParameters["nonce"].ToString());

            var tokenResponse = await oidcClient.PostAsJsonAsync(
                "/api/v1/admin/auth/providers/oidc/token",
                new
                {
                    grantType = "authorization_code",
                    code = "code-123",
                    state = authorizeParameters["state"].ToString()
                });

            tokenResponse.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var sessionResponse = await oidcClient.GetAsync("/api/v1/admin/auth/session");
            var session = await sessionResponse.Content.ReadFromJsonAsync(AdminAuthJsonContext.Default.AdminAuthSessionResponse);
            session!.IsAuthenticated.Should().BeFalse();
        }
        finally
        {
            stubFactory.Reset();
        }
    }
}
