// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// End-to-end coverage for the ArcGIS OAuth2 bridge callback IdP round-trip
/// (#1242/#1484). A mock OIDC provider is wired into the broker's named HTTP client
/// (discovery + token endpoints) and a symmetric signing key validates the relayed
/// id_token, so the test exercises the full authorize → IdP-redirect → callback →
/// token path that previously only had the not-configured 404 gate.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingOAuth2CallbackE2ETests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string ClientId = "arcgispro";
    private const string RedirectUri = "https://app.example.com/oauth/redirect";

    // Mock IdP coordinates. The broker derives the discovery URL from the provider
    // Authority; the stub serves discovery + token regardless of host.
    private const string IdpAuthority = "https://oidc.test";
    private const string IdpClientId = "honua-confidential-client";
    private const string IdpSigningKey = "OidcCallbackE2ESymmetricSigningKey-0123456789-abcdef";

    private readonly WebAppFixture _fixture;

    public SharingOAuth2CallbackE2ETests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");

                // Allow-list the ArcGIS client redirect (#1484 open-redirect mitigation).
                builder.UseSetting("Authentication:PortalToken:OAuth2:AllowedRedirectUris:0", RedirectUri);

                // Opt into the OIDC-backed credential verifier so the relayed id_token
                // is validated by the shared OIDC core.
                builder.UseSetting("Authentication:PortalCredentialVerifier:UseOidc", "true");

                // A Generic OIDC provider with a static symmetric signing key: the
                // verifier validates the id_token without JWKS discovery, and the
                // broker treats Authority as the discovery base for the mock IdP.
                builder.UseSetting("Oidc:Enabled", "true");
                // RequireHttps stays true (the options validator enforces it); the
                // mock IdP authority is https and the broker's HTTP calls are served
                // by the in-process MockOidcProviderHandler, so no real TLS is needed.
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", IdpAuthority);
                builder.UseSetting("Oidc:Generic:ClientId", IdpClientId);
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", IdpSigningKey);
            })
            .ConfigureServices(services =>
            {
                // Intercept the broker's discovery/token HTTP calls with a mock IdP.
                services.AddHttpClient(PortalOAuthBroker.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new MockOidcProviderHandler());
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/callback")]
    public async Task Callback_FullIdpRoundTrip_IssuesArcGisCodeAndMintsToken()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);

        // 1. authorize: creates a broker session and redirects to the (mock) IdP
        //    authorize endpoint carrying the combined state.
        var verifier = "callback-e2e-pkce-verifier-value-abcdefghijklmnopqrstuvwx";
        var challenge = WebEncoders.Base64UrlEncode(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizeUrl = QueryHelpers.AddQueryString("/sharing/rest/oauth2/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["state"] = "client-state-xyz",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        });

        using var authorizeResponse = await client.GetAsync(authorizeUrl);
        authorizeResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var idpRedirect = authorizeResponse.Headers.Location;
        idpRedirect.Should().NotBeNull();
        idpRedirect!.AbsoluteUri.Should().StartWith($"{IdpAuthority}/authorize");

        var idpQuery = QueryHelpers.ParseQuery(idpRedirect.Query);
        var combinedState = idpQuery["state"].ToString();
        combinedState.Should().NotBeNullOrWhiteSpace();

        // 2. callback: the mock IdP returns to Honua with a code + the combined state.
        //    The broker exchanges the IdP code (stub returns a signed id_token),
        //    verifies the principal, and redirects to the allow-listed ArcGIS
        //    redirect_uri with a Honua-minted authorization code.
        var callbackUrl = QueryHelpers.AddQueryString("/sharing/rest/oauth2/callback", new Dictionary<string, string?>
        {
            ["state"] = combinedState,
            ["code"] = "mock-idp-authorization-code",
        });

        using var callbackResponse = await client.GetAsync(callbackUrl);
        callbackResponse.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var clientRedirect = callbackResponse.Headers.Location;
        clientRedirect.Should().NotBeNull();
        clientRedirect!.GetLeftPart(UriPartial.Path).Should().Be(RedirectUri);

        var clientQuery = QueryHelpers.ParseQuery(clientRedirect.Query);
        clientQuery["state"].ToString().Should().Be("client-state-xyz");
        var arcgisCode = clientQuery["code"].ToString();
        arcgisCode.Should().NotBeNullOrWhiteSpace();

        // 3. token: exchange the Honua authorization code (with PKCE) for an access token.
        using var tokenResponse = await client.PostAsync(
            "/sharing/rest/oauth2/token",
            new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("grant_type", "authorization_code"),
                new KeyValuePair<string, string>("code", arcgisCode),
                new KeyValuePair<string, string>("redirect_uri", RedirectUri),
                new KeyValuePair<string, string>("client_id", ClientId),
                new KeyValuePair<string, string>("code_verifier", verifier),
            }));

        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await tokenResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        doc.RootElement.GetProperty("expires_in").GetInt64().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/authorize")]
    public async Task Authorize_NonAllowListedRedirectUri_RejectsWithoutRedirect()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = "https://evil.example.com/steal",
            ["state"] = "client-state-xyz",
            ["code_challenge"] = "abc",
            ["code_challenge_method"] = "S256",
        });

        using var response = await client.GetAsync(url);

        // Open-redirect mitigation: a non-allow-listed redirect_uri is a direct 400
        // and is NEVER redirected to.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Headers.Location.Should().BeNull();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/oauth2/authorize")]
    public async Task Authorize_MissingPkceChallenge_RedirectsErrorToClient()
    {
        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        var url = QueryHelpers.AddQueryString("/sharing/rest/oauth2/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = ClientId,
            ["redirect_uri"] = RedirectUri,
            ["state"] = "client-state-xyz",
            // No code_challenge: hard PKCE requirement rejects.
        });

        using var response = await client.GetAsync(url);

        // The redirect_uri is allow-listed, so per RFC 6749 the error is delivered to
        // the client redirect rather than as a bare 400.
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var location = response.Headers.Location;
        location.Should().NotBeNull();
        location!.GetLeftPart(UriPartial.Path).Should().Be(RedirectUri);
        QueryHelpers.ParseQuery(location.Query)["error"].ToString().Should().Be("invalid_request");
    }

    private static string CreateIdToken()
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(IdpSigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new("sub", "named.user@example.com"),
            new("name", "Named User"),
            new("email", "named.user@example.com"),
            new("roles", "org_user"),
            new(ClaimTypes.Role, "org_user"),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        var token = new JwtSecurityToken(
            issuer: IdpAuthority,
            audience: IdpClientId,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>
    /// Minimal mock OIDC provider: serves the discovery document and the token
    /// endpoint (returning a signed id_token) for the broker's IdP leg.
    /// </summary>
    private sealed class MockOidcProviderHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                var discovery = JsonSerializer.Serialize(new
                {
                    authorization_endpoint = $"{IdpAuthority}/authorize",
                    token_endpoint = $"{IdpAuthority}/token",
                });
                return Task.FromResult(Json(discovery));
            }

            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                var tokenResponse = JsonSerializer.Serialize(new
                {
                    access_token = "mock-idp-access-token",
                    id_token = CreateIdToken(),
                    token_type = "Bearer",
                    expires_in = 600,
                });
                return Task.FromResult(Json(tokenResponse));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage Json(string body)
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
    }
}
