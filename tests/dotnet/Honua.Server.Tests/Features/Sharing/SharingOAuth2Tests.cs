// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Integration tests for the ArcGIS-compatible <c>/sharing/rest/oauth2</c> named-user
/// bridge (#1242). The interactive authorize/callback leg requires a live OIDC
/// provider, so these tests drive the load-bearing minting path directly: a verified
/// authorization code (or refresh token) is seeded into the shared cache-backed store
/// and exchanged through the real <c>oauth2/token</c> endpoint, which mints the
/// access token via <c>IPortalTokenIssuer</c>. The authorize endpoint is covered for
/// its not-configured (404) gate.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingOAuth2Tests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string TokenEndpoint = "/sharing/rest/oauth2/token";
    private const string ClientId = "arcgispro";
    private const string RedirectUri = "https://app.example.com/oauth/redirect";

    private readonly WebAppFixture _fixture;

    public SharingOAuth2Tests()
    {
        _fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeGrantWithPkce_ReturnsAccessTokenAndRefreshToken()
    {
        var verifier = "test-pkce-verifier-value-0123456789-abcdefghij";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenAsync(response);
        payload.AccessToken.Should().NotBeNullOrWhiteSpace();
        payload.ExpiresIn.Should().BeGreaterThan(0);
        payload.RefreshToken.Should().NotBeNullOrWhiteSpace();
        payload.TokenType.Should().Be("Bearer");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_RefreshTokenGrant_ReturnsFreshAccessToken()
    {
        // First obtain a refresh token via the authorization-code grant.
        var verifier = "another-pkce-verifier-value-9876543210-zyxwvut";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var first = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPayload = await ReadTokenAsync(first);
        firstPayload.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using var refreshed = await PostFormAsync(
            client,
            ("grant_type", "refresh_token"),
            ("refresh_token", firstPayload.RefreshToken!),
            ("client_id", ClientId));

        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshedPayload = await ReadTokenAsync(refreshed);
        refreshedPayload.AccessToken.Should().NotBeNullOrWhiteSpace();
        refreshedPayload.ExpiresIn.Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeWithWrongPkceVerifier_ReturnsInvalidGrant()
    {
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes("correct-verifier")));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", "wrong-verifier"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("invalid_grant");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeWithoutRegisteredPkce_ReturnsInvalidGrant()
    {
        // Hard PKCE requirement (#1484): a code minted without a code_challenge is
        // rejected by the token endpoint even if the client sends no verifier. This
        // guards the seam directly (the authorize endpoint also blocks no-PKCE flows).
        var code = await SeedAuthorizationCodeAsync(codeChallenge: null, method: null);

        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("invalid_grant");
        error.ErrorDescription.Should().Contain("PKCE");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_RefreshTokenGrant_RotatesRefreshTokenAndRevokesOld()
    {
        // Refresh-token rotation (#1484): a refresh exchange returns a NEW refresh
        // token and revokes the presented one, so reusing the old token fails.
        var verifier = "rotation-pkce-verifier-value-abcdefghijklmnopqrstuv";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var first = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstPayload = await ReadTokenAsync(first);
        firstPayload.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using var rotated = await PostFormAsync(
            client,
            ("grant_type", "refresh_token"),
            ("refresh_token", firstPayload.RefreshToken!),
            ("client_id", ClientId));
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotatedPayload = await ReadTokenAsync(rotated);
        rotatedPayload.RefreshToken.Should().NotBeNullOrWhiteSpace();
        rotatedPayload.RefreshToken.Should().NotBe(firstPayload.RefreshToken,
            "a rotated refresh token must differ from the one it replaces");

        // The original refresh token is now revoked and must not be accepted again.
        using var replay = await PostFormAsync(
            client,
            ("grant_type", "refresh_token"),
            ("refresh_token", firstPayload.RefreshToken!),
            ("client_id", ClientId));
        replay.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var replayError = await ReadErrorAsync(replay);
        replayError.Error.Should().Be("invalid_grant");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_UnknownAuthorizationCode_ReturnsInvalidGrant()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", "00000000000000000000000000000000"),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", "whatever"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("invalid_grant");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_UnsupportedGrantType_ReturnsError()
    {
        // client_credentials is off by default (ADR-0053): the default fixture does
        // NOT enable it, so the grant is rejected exactly as before. This is the
        // no-behaviour-change-by-default regression guard.
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "client_credentials"),
            ("client_id", ClientId));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("unsupported_grant_type");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_CommunityOidcGrants_DeniesOidcWithoutBlockingClientCredentials()
    {
        var fixture = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Community)
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:OAuth2:EnableClientCredentials", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var oidcResponse = await PostFormAsync(
                client,
                ("grant_type", "authorization_code"),
                ("code", "unlicensed-code"));

            await oidcResponse.AssertGeoServicesErrorAsync(
                (int)HttpStatusCode.PaymentRequired);
            (await oidcResponse.Content.ReadAsStringAsync())
                .Should().Contain(FeatureCatalog.OidcAuthenticationKey);

            using var refreshResponse = await PostFormAsync(
                client,
                ("grant_type", "refresh_token"),
                ("refresh_token", "unlicensed-refresh-token"));

            await refreshResponse.AssertGeoServicesErrorAsync(
                (int)HttpStatusCode.PaymentRequired);
            (await refreshResponse.Content.ReadAsStringAsync())
                .Should().Contain(FeatureCatalog.OidcAuthenticationKey);

            using var clientCredentialsResponse = await PostFormAsync(
                client,
                ("grant_type", "client_credentials"),
                ("client_id", "service-client"),
                ("client_secret", "not-a-real-secret"));

            clientCredentialsResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = await ReadErrorAsync(clientCredentialsResponse);
            error.Error.Should().Be("invalid_client");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_ClientCredentialsGrant_WhenEnabledWithValidSecret_ReachesIssuanceAndBindsToClientIp()
    {
        // Opt in to the client_credentials grant (ADR-0053, #1860) and provision an
        // API key whose value is the client_secret, then exchange it end-to-end
        // through the real HTTP endpoint. The grant binds the issued token to the
        // calling client's IP (PortalTokenClientType.Ip); the in-process TestServer
        // does NOT populate Connection.RemoteIpAddress, so the endpoint correctly
        // returns invalid_request here rather than minting an unbound token. This
        // proves the flag is honored and the request reaches the IP-binding step;
        // the full happy path (valid secret + resolvable IP -> opaque IP-bound token
        // carrying the key's permissions, no refresh token) is covered without a
        // TestServer by PortalOAuthClientCredentialsTests.
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:OAuth2:EnableClientCredentials", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            var apiKeyStore = fixture.Services.GetRequiredService<IAdminApiKeyStore>();
            var created = await apiKeyStore.CreateAsync(
                name: "etl-worker",
                permissions: ["services:read"],
                expiresAt: null,
                createdBy: "test",
                CancellationToken.None);

            using var client = fixture.CreateClient();
            using var tokenRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", "etl-worker"),
                new KeyValuePair<string, string>("client_secret", created.Key),
            });
            using var response = await client.PostAsync(
                "/sharing/rest/oauth2/token",
                tokenRequestContent);

            // A valid secret authenticated (so it is not invalid_client); issuance was
            // reached and only the IP binding could not be resolved under TestServer.
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = await ReadErrorAsync(response);
            error.Error.Should().Be("invalid_request");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_ClientCredentialsGrant_WhenEnabledWithBadSecret_ReturnsInvalidClient()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:OAuth2:EnableClientCredentials", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var tokenRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "client_credentials"),
                new KeyValuePair<string, string>("client_id", "etl-worker"),
                new KeyValuePair<string, string>("client_secret", "hnua_not-a-real-key"),
            });
            using var response = await client.PostAsync(
                "/sharing/rest/oauth2/token",
                tokenRequestContent);

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var error = await ReadErrorAsync(response);
            error.Error.Should().Be("invalid_client");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeWithMismatchedRedirectUri_ReturnsInvalidGrant()
    {
        var verifier = "redirect-mismatch-verifier-value-abcdefghij";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", "https://evil.example.com/redirect"),
            ("client_id", ClientId),
            ("code_verifier", verifier));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("invalid_grant");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeGrantViaGetQueryString_Returns405MethodNotAllowed()
    {
        // BH7-001: The token endpoint must be POST-only per RFC 6749 §4.1.3.
        // A GET registration exposed auth codes, client_secret, and refresh
        // tokens in URL query strings — logged by proxies/CDN/access logs (CWE-598).
        // Verify that GET requests are rejected with 405 Method Not Allowed.
        var verifier = "query-string-grant-verifier-value-abcdefghijklmnop";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/token", new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        using var response = await client.GetAsync(url);

        // GET must be rejected — credentials must never travel in the URL.
        await response.AssertGeoServicesErrorAsync(405);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/callback")]
    public async Task Callback_WhenNoOidcProviderConfigured_Returns404()
    {
        using var client = _fixture.CreateClient();
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/callback", new Dictionary<string, string?>
        {
            ["state"] = "idpstate.brokersession",
            ["code"] = "idp-code",
        });
        using var response = await client.GetAsync(url);

        // No OIDC provider is configured in the Test environment, so the callback
        // surface 404s exactly like authorize.
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/authorize")]
    public async Task Authorize_WhenNoOidcProviderConfigured_Returns404()
    {
        using var client = _fixture.CreateClient();
        // Use the literal route path (not the AuthorizeEndpoint const) so the
        // endpoint-registry governance scanner recognises this as a same-method
        // HTTP request that backs GET /sharing/rest/oauth2/authorize.
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["state"] = "abc",
        });
        using var response = await client.GetAsync(url);

        // The Test environment configures no OIDC provider, so there is no named-user
        // identity to broker and the surface 404s rather than silently redirecting.
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_WhenEnabledForUnknownToken_ReturnsActiveFalse()
    {
        // RFC 7662 introspection (#1890) is off by default; opt in and drive the real
        // admin-guarded endpoint. An unknown/forged token must introspect as
        // { active: false } per RFC 7662 Â§2.2 â€” no other detail is leaked.
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:OAuth2:Jwt:EnableIntrospection", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            // Use the literal route path (not a const) so the endpoint-registry governance
            // scanner recognises this as a same-method (POST) HTTP request that backs
            // POST /sharing/rest/oauth2/introspect.
            using var introspectRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("token", "00000000000000000000000000000000"),
            });
            using var response = await client.PostAsync(
                "/sharing/rest/oauth2/introspect",
                introspectRequestContent);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);
            document.RootElement.GetProperty("active").GetBoolean().Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_WhenDisabled_Returns404()
    {
        // The introspection surface is absent (404) unless explicitly enabled, so it is
        // never a silent discovery surface (RFC 7662 Â§4). The default fixture does not
        // enable it; the admin-authenticated request still 404s.
        using var client = _fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
        using var introspectRequestContent = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("token", "x"),
        });
        using var response = await client.PostAsync(
            "/sharing/rest/oauth2/introspect",
            introspectRequestContent);

        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/token")]
    public async Task Token_WhenPortalTokenDisabled_Returns404()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:Enabled", "false");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var tokenRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", "x"),
            });
            using var response = await client.PostAsync(
                TokenEndpoint,
                tokenRequestContent);

            await response.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/revoke")]
    public async Task Revoke_AccessToken_SubsequentValidationFails()
    {
        // Per-token revocation (#2155): an access token that validates today must fail
        // the very next authorization check after it is revoked. The opaque token is
        // bound to the redirect host, so validation is checked through the real
        // IPortalTokenIssuer with a matching referer binding before and after revoke.
        var verifier = "revoke-access-verifier-value-abcdefghijklmnopqrstuvwxyz";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var issued = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenAsync(issued);
        payload.AccessToken.Should().NotBeNullOrWhiteSpace();

        var issuer = _fixture.Services.GetRequiredService<IPortalTokenIssuer>();
        var binding = new PortalTokenBinding(RedirectUri, null);

        var before = await issuer.ValidateAsync(payload.AccessToken, binding, CancellationToken.None);
        before.Should().NotBeNull("the freshly issued access token must validate before revocation");

        using var revoke = await PostRevokeAsync(client, ("token", payload.AccessToken));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await issuer.ValidateAsync(payload.AccessToken, binding, CancellationToken.None);
        after.Should().BeNull("a revoked access token must fail every subsequent authorization check");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/revoke")]
    public async Task Revoke_RefreshToken_SubsequentRefreshFails()
    {
        // Revoking a refresh token closes the silent re-issuance path end-to-end: the
        // refresh_token grant must fail once the token is revoked (#2155).
        var verifier = "revoke-refresh-verifier-value-abcdefghijklmnopqrstuvwxyz";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        using var issued = await PostFormAsync(
            client,
            ("grant_type", "authorization_code"),
            ("code", code),
            ("redirect_uri", RedirectUri),
            ("client_id", ClientId),
            ("code_verifier", verifier));
        issued.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenAsync(issued);
        payload.RefreshToken.Should().NotBeNullOrWhiteSpace();

        using var revoke = await PostRevokeAsync(
            client,
            ("token", payload.RefreshToken!),
            ("token_type_hint", "refresh_token"));
        revoke.StatusCode.Should().Be(HttpStatusCode.OK);

        using var refresh = await PostFormAsync(
            client,
            ("grant_type", "refresh_token"),
            ("refresh_token", payload.RefreshToken!),
            ("client_id", ClientId));
        refresh.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(refresh);
        error.Error.Should().Be("invalid_grant");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/revoke")]
    public async Task Revoke_UnknownToken_Returns200()
    {
        // RFC 7009 Â§2.2: a revocation request for an unknown/already-revoked token still
        // returns 200, so the endpoint can never be used to probe token validity.
        using var client = _fixture.CreateClient();
        using var response = await PostRevokeAsync(client, ("token", "00000000000000000000000000000000"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/revoke")]
    public async Task Revoke_MissingToken_ReturnsInvalidRequest()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostRevokeAsync(client, ("token_type_hint", "access_token"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var error = await ReadErrorAsync(response);
        error.Error.Should().Be("invalid_request");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/revoke")]
    public async Task Revoke_WhenPortalTokenDisabled_Returns404()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:Enabled", "false");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var revokeRequestContent = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("token", "x"),
            });
            using var response = await client.PostAsync(
                "/sharing/rest/oauth2/revoke",
                revokeRequestContent);

            await response.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> PostRevokeAsync(HttpClient client, params (string Key, string Value)[] pairs)
    {
        using var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        // Use the literal route path so the endpoint-registry governance scanner
        // recognises this as a same-method (POST) request backing POST
        // /sharing/rest/oauth2/revoke.
        return await client.PostAsync("/sharing/rest/oauth2/revoke", content);
    }

    private async Task<string> SeedAuthorizationCodeAsync(string? codeChallenge, string? method)
    {
        // PortalOAuthStore is the shared singleton the endpoint resolves, so a code
        // seeded here is the same one the token service consumes.
        var store = _fixture.Services.GetRequiredService<PortalOAuthStore>();
        var principal = new PortalCredentialPrincipal("named.user@example.com", "Named User", null, ["org_user"]);
        var record = new PortalOAuthAuthorizationCode
        {
            ClientId = ClientId,
            RedirectUri = RedirectUri,
            CodeChallenge = codeChallenge,
            CodeChallengeMethod = method,
            ExpirationMinutes = null,
            Principal = PortalOAuthPrincipal.From(principal),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        return await store.CreateAuthorizationCodeAsync(record, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, params (string Key, string Value)[] pairs)
    {
        using var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        // Use the literal route path (not the TokenEndpoint const) so the endpoint-registry
        // governance scanner recognises this as a same-method (POST) HTTP request that backs
        // POST /sharing/rest/oauth2/token.
        return await client.PostAsync("/sharing/rest/oauth2/token", content);
    }

    private static async Task<TokenPayload> ReadTokenAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TokenPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty token response body.");
    }

    private static async Task<ErrorPayload> ReadErrorAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<ErrorPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty error response body.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record TokenPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("access_token")]
        public string AccessToken { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("expires_in")]
        public long ExpiresIn { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("token_type")]
        public string TokenType { get; init; } = string.Empty;
    }

    private sealed record ErrorPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("error")]
        public string Error { get; init; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }
}
