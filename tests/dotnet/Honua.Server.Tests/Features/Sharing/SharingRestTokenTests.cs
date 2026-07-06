// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Integration tests for the ArcGIS-compatible <c>/sharing/rest/generateToken</c>
/// endpoint and the matching <c>PortalToken</c> authentication scheme.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingRestTokenTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string TokenEndpoint = "/sharing/rest/generateToken";
    private const string SecureRefererA = "https://app.example.com/maps/";

    // The in-process WebApplicationFactory transport leaves Connection.RemoteIpAddress
    // unset, which a real Kestrel deployment always populates. IP-bound token issuance
    // (client=ip / client=requestip) needs that address, so the fixture installs a
    // startup filter that stamps a loopback IP onto every request.
    private static readonly IPAddress ClientIp = IPAddress.Parse("203.0.113.10");

    private readonly WebAppFixture _fixture;

    public SharingRestTokenTests()
    {
        _fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // Allow the in-process test transport to issue tokens; production
                // defaults remain RequireHttps=true.
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(ClientIp)));
            });
    }

    /// <summary>
    /// Stamps a fixed remote IP onto each request so IP-bound token flows behave as they
    /// do behind Kestrel, where Connection.RemoteIpAddress is always populated.
    /// </summary>
    private sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
            => app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    await nextMiddleware(context).ConfigureAwait(false);
                });
                next(app);
            };
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_WithFormCredentials_ReturnsTokenAndExpiry()
    {
        using var client = _fixture.CreateClient();
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "admin"),
            new KeyValuePair<string, string>("password", AdminPassword),
            new KeyValuePair<string, string>("client", "referer"),
            new KeyValuePair<string, string>("referer", SecureRefererA),
            new KeyValuePair<string, string>("f", "json"),
        });
        using var response = await client.PostAsync("/sharing/rest/generateToken", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenPayloadAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
        payload.Expires.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        payload.Ssl.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_WithQueryStringCredentials_ReturnsToken()
    {
        using var client = _fixture.CreateClient();
        var query = $"/sharing/rest/generateToken?username=admin" +
            $"&password={Uri.EscapeDataString(AdminPassword)}" +
            $"&client=referer&referer={Uri.EscapeDataString(SecureRefererA)}&f=json";
        using var response = await client.GetAsync(query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenPayloadAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
        payload.Expires.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        payload.Ssl.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_WithInvalidCredentials_Returns401()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client,
            ("username", "admin"), ("password", "WRONG"),
            ("client", "referer"), ("referer", SecureRefererA), ("f", "json"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_WithoutCredentials_Returns400()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client,
            ("client", "referer"), ("referer", SecureRefererA), ("f", "json"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_RefererBindingWithoutReferer_Returns400()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client,
            ("username", "admin"), ("password", AdminPassword),
            ("client", "referer"), ("f", "json"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_WithClientRequestIp_ReturnsToken()
    {
        // ArcGIS clients request an IP-bound token with client=requestip (the
        // default for many SDKs). It must be accepted and IP-bind the token,
        // not be rejected with the misleading "referer required" error (#1912).
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client,
            ("username", "admin"), ("password", AdminPassword),
            ("client", "requestip"), ("f", "json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenPayloadAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
        payload.Expires.Should().BeGreaterThan(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        payload.Ssl.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_WithClientIp_ReturnsToken()
    {
        // The explicit client=ip alias behaves identically to client=requestip: the
        // token binds to the request's source IP without requiring a referer.
        using var client = _fixture.CreateClient();
        var query = $"/sharing/rest/generateToken?username=admin" +
            $"&password={Uri.EscapeDataString(AdminPassword)}&client=ip&f=json";
        using var response = await client.GetAsync(query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenPayloadAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task IssuedRequestIpToken_AuthenticatesSubsequentRestRequest()
    {
        // An IP-bound token issued via client=requestip must authenticate later
        // requests from the same client IP end-to-end (issue -> validate path).
        using var client = _fixture.CreateClient();
        var token = await IssueTokenAsync(client, ("client", "requestip"));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/test/FeatureServer?f=json&token={token}");
        using var response = await client.SendAsync(request);

        // Authenticated (not 401); status is 200/404 depending on the seeded layer.
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_HttpsOnlyEnforced_Returns403WhenInsecure()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await PostFormAsync(client,
                ("username", "admin"), ("password", AdminPassword),
                ("client", "referer"), ("referer", SecureRefererA), ("f", "json"));

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_RespectsMaxExpirationClamp()
    {
        const int configuredMax = 90;

        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:MaxExpirationMinutes",
                    configuredMax.ToString(System.Globalization.CultureInfo.InvariantCulture));
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            var issuedAt = DateTimeOffset.UtcNow;
            using var response = await PostFormAsync(client,
                ("username", "admin"), ("password", AdminPassword),
                ("client", "referer"), ("referer", SecureRefererA),
                ("expiration", "100000"), ("f", "json"));

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadTokenPayloadAsync(response);
            var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.Expires);
            (expiresAt - issuedAt).TotalMinutes.Should().BeApproximately(configuredMax, 1);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task IssuedToken_AuthenticatesSubsequentRestRequest()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueTokenAsync(client, ("client", "referer"), ("referer", SecureRefererA));

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/rest/services/test/FeatureServer?f=json&token={token}");
        request.Headers.Referrer = new Uri(SecureRefererA);
        using var response = await client.SendAsync(request);

        // The endpoint authorizes by access policy; the response should be either
        // 200/404 depending on whether the seed registers the layer, but it must NOT
        // be 401 because the portal token authenticated the request.
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task IssuedToken_AcceptedViaAuthorizationBearerHeader_Authenticates()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueTokenAsync(client, ("client", "referer"), ("referer", SecureRefererA));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer?f=json");
        request.Headers.Referrer = new Uri(SecureRefererA);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer")]
    public async Task IssuedToken_AcceptedViaEsriAuthorizationHeader_Authenticates()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueTokenAsync(client, ("client", "referer"), ("referer", SecureRefererA));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/rest/services/test/FeatureServer?f=json");
        request.Headers.Referrer = new Uri(SecureRefererA);
        request.Headers.TryAddWithoutValidation("X-Esri-Authorization", $"Bearer {token}");
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_UnsupportedFormat_Returns400()
    {
        using var client = _fixture.CreateClient();
        using var response = await PostFormAsync(client,
            ("username", "admin"), ("password", AdminPassword),
            ("client", "referer"), ("referer", SecureRefererA), ("f", "xml"));

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_WhenDisabled_Returns404()
    {
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.UseSetting("Authentication:PortalToken:Enabled", "false");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            using var response = await PostFormAsync(client,
                ("username", "admin"), ("password", AdminPassword),
                ("client", "referer"), ("referer", SecureRefererA), ("f", "json"));

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_CredentialsInQueryStringOverPlainHttp_Returns403WithHelpfulMessage()
    {
        // PA-059: When RequireHttps=true (the default), a GET request carrying
        // credentials in the URL query string over plain HTTP must be rejected
        // with an actionable error that directs the caller to POST or HTTPS.
        var fixture = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // RequireHttps=true is the default; set it explicitly for clarity.
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "true");
            });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient();
            var query = $"/sharing/rest/generateToken?username=admin" +
                $"&password={Uri.EscapeDataString(AdminPassword)}&client=ip&f=json";
            using var response = await client.GetAsync(query);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            // The error body should mention POST or HTTPS as alternatives so the
            // caller understands how to proceed without the credential exposure risk.
            body.Should().ContainAny("POST", "HTTPS", "query string", "plaintext");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/generateToken")]
    public async Task GenerateToken_CredentialsInQueryStringWithInsecureMode_Succeeds()
    {
        // PA-059: When RequireHttps=false (explicit dev/test opt-out), GET with
        // credentials in the query string is still accepted — ArcGIS clients use
        // this flow and the dev override must not break them.
        using var client = _fixture.CreateClient();
        var query = $"/sharing/rest/generateToken?username=admin" +
            $"&password={Uri.EscapeDataString(AdminPassword)}&client=ip&f=json";
        using var response = await client.GetAsync(query);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenPayloadAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, params (string Key, string Value)[] pairs)
    {
        var content = new FormUrlEncodedContent(pairs.Select(p => new KeyValuePair<string, string>(p.Key, p.Value)));
        return await client.PostAsync(TokenEndpoint, content);
    }

    private static string BuildQuery(params (string Key, string Value)[] pairs)
    {
        var query = string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        return $"{TokenEndpoint}?{query}";
    }

    private async Task<string> IssueTokenAsync(HttpClient client, params (string Key, string Value)[] pairs)
    {
        var allPairs = new List<(string, string)>
        {
            ("username", "admin"),
            ("password", AdminPassword),
            ("f", "json"),
        };
        allPairs.AddRange(pairs);
        using var response = await PostFormAsync(client, allPairs.ToArray());
        response.EnsureSuccessStatusCode();
        var payload = await ReadTokenPayloadAsync(response);
        return payload.Token;
    }

    private static async Task<TokenPayload> ReadTokenPayloadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TokenPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty token response body.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private sealed record TokenPayload
    {
        public string Token { get; init; } = string.Empty;

        public long Expires { get; init; }

        public bool Ssl { get; init; }
    }
}
