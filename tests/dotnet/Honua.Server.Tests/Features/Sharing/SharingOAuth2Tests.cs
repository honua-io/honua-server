// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
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
    [Endpoint("GET /sharing/rest/oauth2/token")]
    public async Task Token_AuthorizationCodeGrantViaQueryString_ReturnsAccessToken()
    {
        var verifier = "query-string-grant-verifier-value-abcdefghijklmnop";
        var challenge = WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var code = await SeedAuthorizationCodeAsync(challenge, "S256");

        using var client = _fixture.CreateClient();
        // Use the literal route path (not the TokenEndpoint const) so the
        // endpoint-registry governance scanner recognises this as a same-method
        // (GET) HTTP request that backs GET /sharing/rest/oauth2/token.
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/token", new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri,
            ["client_id"] = ClientId,
            ["code_verifier"] = verifier,
        });
        using var response = await client.GetAsync(url);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenAsync(response);
        payload.AccessToken.Should().NotBeNullOrWhiteSpace();
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
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
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
            using var response = await client.PostAsync(
                TokenEndpoint,
                new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "authorization_code"),
                    new KeyValuePair<string, string>("code", "x"),
                }));

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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
        var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        return await client.PostAsync(TokenEndpoint, content);
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
