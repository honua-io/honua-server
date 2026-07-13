// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// Integration tests for the optional RFC 7662 token introspection endpoint
/// (<c>POST /sharing/rest/oauth2/introspect</c>) and the optional JWT access-token
/// format (ADR-0054, #1890). Both are off by default; these enable them explicitly
/// and prove valid/expired/revoked introspection plus JWT signature validation.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class SharingOAuth2IntrospectionTests
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string IntrospectPath = "/sharing/rest/oauth2/introspect";
    private const string SigningKey = "introspection-test-signing-key-0123456789-abcdef";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static WebAppFixture CreateFixture(bool enableJwt = false)
        => new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
            builder.UseSetting("Authentication:PortalToken:OAuth2:Jwt:EnableIntrospection", "true");
            if (enableJwt)
            {
                builder.UseSetting("Authentication:PortalToken:OAuth2:Jwt:EnableJwtAccessTokens", "true");
                builder.UseSetting("Authentication:PortalToken:OAuth2:Jwt:SigningKey", SigningKey);
            }
        });

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_ActiveOpaqueToken_ReturnsActiveTrueWithMetadata()
    {
        var fixture = CreateFixture();
        await fixture.InitializeAsync();
        try
        {
            var issuer = fixture.Services.GetRequiredService<IPortalTokenIssuer>();
            var issuance = await issuer.IssueAsync(
                new PortalTokenIssueRequest(
                    PrincipalId: "etl-worker",
                    DisplayName: "ETL Worker",
                    TenantId: null,
                    Roles: ["services:read"],
                    ClientType: PortalTokenClientType.Ip,
                    BindingValue: "203.0.113.10",
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
                CancellationToken.None);

            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, issuance.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadAsync(response);
            payload.Active.Should().BeTrue();
            payload.Sub.Should().Be("etl-worker");
            payload.Scope.Should().Contain("services:read");
            payload.Exp.Should().BeGreaterThan(0);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_UnknownToken_ReturnsActiveFalse()
    {
        var fixture = CreateFixture();
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, "0000000000000000000000000000000000000000000000000000000000000000");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadAsync(response);
            payload.Active.Should().BeFalse();
            // RFC 7662 §2.2: an inactive token leaks nothing else.
            payload.Sub.Should().BeNull();
            payload.Scope.Should().BeNull();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_ExpiredToken_ReturnsActiveFalse()
    {
        var fixture = CreateFixture();
        await fixture.InitializeAsync();
        try
        {
            var issuer = fixture.Services.GetRequiredService<IPortalTokenIssuer>();
            // Issue an already-expired token; the cache-backed issuer evicts on read,
            // so introspection sees it as inactive.
            var issuance = await issuer.IssueAsync(
                new PortalTokenIssueRequest(
                    PrincipalId: "expired",
                    DisplayName: "Expired",
                    TenantId: null,
                    Roles: [],
                    ClientType: PortalTokenClientType.Ip,
                    BindingValue: "203.0.113.10",
                    ExpiresAt: DateTimeOffset.UtcNow.AddSeconds(-1)),
                CancellationToken.None);

            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, issuance.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadAsync(response);
            payload.Active.Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_ValidJwtAccessToken_ReturnsActiveTrue()
    {
        var fixture = CreateFixture(enableJwt: true);
        await fixture.InitializeAsync();
        try
        {
            var jwtService = fixture.Services.GetRequiredService<PortalJwtAccessTokenService>();
            jwtService.JwtEnabled.Should().BeTrue("the fixture enables JWT with a usable signing key");

            var issuance = await jwtService.IssueAsync(
                new PortalTokenIssueRequest(
                    PrincipalId: "jwt-user",
                    DisplayName: "JWT User",
                    TenantId: null,
                    Roles: ["services:read"],
                    ClientType: PortalTokenClientType.Ip,
                    BindingValue: "203.0.113.10",
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
                CancellationToken.None);

            // The minted access token is a signed JWT (three dot-separated segments).
            issuance.Token.Split('.').Should().HaveCount(3);

            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, issuance.Token);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadAsync(response);
            payload.Active.Should().BeTrue();
            payload.Sub.Should().Be("jwt-user");
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task Introspect_TamperedJwt_ReturnsActiveFalse()
    {
        var fixture = CreateFixture(enableJwt: true);
        await fixture.InitializeAsync();
        try
        {
            var jwtService = fixture.Services.GetRequiredService<PortalJwtAccessTokenService>();
            var issuance = await jwtService.IssueAsync(
                new PortalTokenIssueRequest(
                    PrincipalId: "jwt-user",
                    DisplayName: "JWT User",
                    TenantId: null,
                    Roles: ["services:read"],
                    ClientType: PortalTokenClientType.Ip,
                    BindingValue: "203.0.113.10",
                    ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(30)),
                CancellationToken.None);

            // Flip the last character of the signature segment to forge the token.
            var tampered = issuance.Token[..^1] + (issuance.Token[^1] == 'a' ? 'b' : 'a');

            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, tampered);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var payload = await ReadAsync(response);
            payload.Active.Should().BeFalse("a forged-signature JWT must never introspect as active");
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
        // Default fixture does NOT enable introspection.
        var fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
            builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
        });
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", AdminPassword));
            using var response = await PostIntrospectAsync(client, "any-token");

            await response.AssertGeoServicesErrorAsync(404);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<HttpResponseMessage> PostIntrospectAsync(HttpClient client, string token)
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("token", token),
        });
        return await client.PostAsync(IntrospectPath, content);
    }

    private static async Task<IntrospectionPayload> ReadAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<IntrospectionPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty introspection response body.");
    }

    private sealed record IntrospectionPayload
    {
        [System.Text.Json.Serialization.JsonPropertyName("active")]
        public bool Active { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("sub")]
        public string? Sub { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("scope")]
        public string? Scope { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("exp")]
        public long? Exp { get; init; }
    }
}
