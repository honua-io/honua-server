// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Middleware;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// #5492: an API key is accepted wherever a GeoServices access token is accepted — the
/// <c>token</c> query parameter, the <c>token</c> form field, and
/// <c>X-Esri-Authorization: Bearer</c> — and carries exactly the authority it carries in
/// the <c>X-API-Key</c> header: never more, and recorded as an API-key credential.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class ApiKeyTokenParameterTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string PortalSelfPath = "/sharing/rest/portals/self";
    private const int ProtectedLayerId = 0;
    private const string AdminOnlyRole = "admin";
    private const string CountParameters = "where=1%3D1&returnCountOnly=true&returnGeometry=false&f=json";
    private const string ManagedKeyName = "desktop-api-key";

    private const string QueryTransport = "query";
    private const string FormTransport = "form";
    private const string EsriHeaderTransport = "x-esri-authorization";

    private const string BootstrapKey = "bootstrap";
    private const string ManagedKey = "managed";

    private static readonly string ProtectedQueryPath =
        $"/rest/services/{WebAppFixture.TestServiceId}/FeatureServer/{ProtectedLayerId}/query";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

    public static TheoryData<string, string> KeyTransportMatrix()
    {
        var data = new TheoryData<string, string>();
        foreach (var keyKind in new[] { BootstrapKey, ManagedKey })
        {
            foreach (var transport in new[] { QueryTransport, FormTransport, EsriHeaderTransport })
            {
                data.Add(keyKind, transport);
            }
        }

        return data;
    }

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _fixture.UpdateV2ResourceMetadata(
            ProtectedLayerId,
            accessPolicy: new AccessPolicy { AllowAnonymous = false, AllowedRoles = [AdminOnlyRole] });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// The issue's first exchange: a client signing in with an API key checks it with
    /// <c>GET /sharing/rest/portals/self?f=json&amp;token=&lt;key&gt;</c>. It must describe the
    /// key's user, not answer 498.
    /// </summary>
    [IntegrationTheory]
    [InlineData(BootstrapKey, QueryTransport)]
    [InlineData(ManagedKey, QueryTransport)]
    [InlineData(BootstrapKey, EsriHeaderTransport)]
    [InlineData(ManagedKey, EsriHeaderTransport)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_ApiKeyPresentedAsToken_DescribesTheKeyUser(string keyKind, string transport)
    {
        var (key, expectedUser) = await CreateKeyAsync(keyKind, ["admin:*"]);
        using var client = _fixture.CreateClient();

        using var response = await SendAsync(client, HttpMethod.Get, PortalSelfPath, "f=json", key, transport);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.TryGetProperty("error", out _).Should().BeFalse("an API key is a valid token: {0}", body);
        var user = json.RootElement.GetProperty("user");
        user.ValueKind.Should().Be(JsonValueKind.Object, body);
        user.GetProperty("username").GetString().Should().Be(expectedUser);
    }

    /// <summary>
    /// The issue's data exchange: the protected layer's query answers a key presented as a
    /// token exactly as it answers the same key in <c>X-API-Key</c>.
    /// </summary>
    [IntegrationTheory]
    [MemberData(nameof(KeyTransportMatrix))]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ProtectedQuery_ApiKeyPresentedAsToken_AnswersAsXApiKeyDoes(string keyKind, string transport)
    {
        var (key, _) = await CreateKeyAsync(keyKind, ["admin:*"]);
        using var client = _fixture.CreateClient();

        using var anonymous = await client.GetAsync($"{ProtectedQueryPath}?{CountParameters}");
        (await ReadCountAsync(anonymous)).Should().BeNull("the layer under test must refuse anonymous reads");

        var expected = await CountViaXApiKeyAsync(client, key);
        expected.Should().BeGreaterThan(0, "the protected layer must hold rows for the comparison to mean anything");

        using var response = await SendAsync(client, HttpMethod.Get, ProtectedQueryPath, CountParameters, key, transport);
        var count = await ReadCountAsync(response);

        count.Should().Be(expected, "a {0} API key presented via {1} must read what X-API-Key reads", keyKind, transport);
    }

    /// <summary>
    /// A key presented as a token carries only its own scope: a non-admin key is refused the
    /// admin-only layer with the same status it gets through <c>X-API-Key</c>, and is never
    /// mistaken for an invalid token.
    /// </summary>
    [IntegrationTheory]
    [InlineData(QueryTransport)]
    [InlineData(FormTransport)]
    [InlineData(EsriHeaderTransport)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task ProtectedQuery_ScopedApiKeyPresentedAsToken_IsNotWidened(string transport)
    {
        var (key, _) = await CreateKeyAsync(ManagedKey, ["read:unrelated-service"]);
        using var client = _fixture.CreateClient();

        using var viaHeader = new HttpRequestMessage(HttpMethod.Get, $"{ProtectedQueryPath}?{CountParameters}");
        viaHeader.Headers.Add("X-API-Key", key);
        using var headerResponse = await client.SendAsync(viaHeader);
        var headerBody = await headerResponse.Content.ReadAsStringAsync();
        headerResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden, headerBody);

        using var response = await SendAsync(client, HttpMethod.Get, ProtectedQueryPath, CountParameters, key, transport);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(headerResponse.StatusCode, body);
        (await ReadCountAsync(response)).Should().BeNull("a scoped key must not read an admin-only layer");
        ReadErrorCode(body).Should().NotBe(498, "the key is valid; only its scope is insufficient: {0}", body);
    }

    /// <summary>
    /// Revoked and expired keys stay invalid tokens: the 498 answer is unchanged.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /sharing/rest/portals/self")]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    public async Task RevokedOrExpiredApiKeyPresentedAsToken_RemainsInvalidToken()
    {
        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var revoked = await store.CreateAsync("revoked-key", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(5), "test", CancellationToken.None);
        await store.RevokeAsync(revoked.Record.Id, CancellationToken.None);
        var expired = await store.CreateAsync("expired-key", ["admin:*"],
            DateTimeOffset.UtcNow.AddMinutes(-1), "test", CancellationToken.None);
        using var client = _fixture.CreateClient();

        foreach (var key in new[] { revoked.Key, expired.Key })
        {
            foreach (var (path, parameters) in new[] { (PortalSelfPath, "f=json"), (ProtectedQueryPath, CountParameters) })
            {
                using var response = await SendAsync(client, HttpMethod.Get, path, parameters, key, QueryTransport);
                var body = await response.Content.ReadAsStringAsync();
                ReadErrorCode(body).Should().Be(498, body);
            }
        }
    }

    /// <summary>
    /// The token transports belong to the GeoServices surfaces. They do not turn a key into
    /// an admin-API credential: the admin API still requires <c>X-API-Key</c>.
    /// </summary>
    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/api-keys")]
    public async Task AdminApi_ApiKeyPresentedAsToken_StillRequiresXApiKey()
    {
        using var client = _fixture.CreateClient();

        using var response = await client.GetAsync($"/api/v1/admin/api-keys?token={Uri.EscapeDataString(AdminPassword)}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<(string Key, string ExpectedUser)> CreateKeyAsync(string keyKind, IReadOnlyList<string> permissions)
    {
        if (keyKind == BootstrapKey)
        {
            return (AdminPassword, "admin");
        }

        var store = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
        var created = await store.CreateAsync(ManagedKeyName, permissions,
            DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        return (created.Key, ManagedKeyName);
    }

    private static async Task<long> CountViaXApiKeyAsync(HttpClient client, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ProtectedQueryPath}?{CountParameters}");
        request.Headers.Add("X-API-Key", key);
        using var response = await client.SendAsync(request);
        var count = await ReadCountAsync(response);
        count.Should().NotBeNull("X-API-Key must read the protected layer: {0}", await response.Content.ReadAsStringAsync());
        return count!.Value;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        string parameters,
        string key,
        string transport)
    {
        HttpRequestMessage request;
        switch (transport)
        {
            case QueryTransport:
                request = new HttpRequestMessage(method, $"{path}?{parameters}&token={Uri.EscapeDataString(key)}");
                break;
            case FormTransport:
                // GeoServices POST transport: every parameter, the token included, in a
                // URL-encoded form body.
                request = new HttpRequestMessage(HttpMethod.Post, path)
                {
                    Content = new StringContent(
                        $"{parameters}&token={Uri.EscapeDataString(key)}",
                        System.Text.Encoding.UTF8,
                        "application/x-www-form-urlencoded"),
                };
                break;
            case EsriHeaderTransport:
                request = new HttpRequestMessage(method, $"{path}?{parameters}");
                request.Headers.TryAddWithoutValidation("X-Esri-Authorization", $"Bearer {key}");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(transport), transport, "Unknown token transport.");
        }

        using (request)
        {
            return await client.SendAsync(request);
        }
    }

    private static int? ReadErrorCode(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) &&
                error.TryGetProperty("code", out var code) &&
                code.ValueKind == JsonValueKind.Number
                ? code.GetInt32()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<long?> ReadCountAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (response.StatusCode != HttpStatusCode.OK)
        {
            return null;
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("count", out var count) ? count.GetInt64() : null;
    }
}

/// <summary>
/// #5492: the principal a token-presented API key yields is the principal the
/// <c>X-API-Key</c> header yields, so the audit trail records an API-key credential.
/// </summary>
public sealed class ApiKeyTokenParameterPrincipalTests
{
    private const string AdminPassword = "ApiKeyTokenParameter-AdminPassword123!";

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task TokenPresentedManagedKey_YieldsTheXApiKeyPrincipal_AndAuditsAsApiKey()
    {
        var store = new InMemoryAdminApiKeyStore();
        var created = await store.CreateAsync("desktop-api-key", ["read:test"],
            DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        var dependencies = CreateDependencies(store);

        var viaHeader = await AuthenticateViaXApiKeyAsync(dependencies, created.Key);
        var viaToken = await AuthenticateViaTokenAsync(dependencies, $"?f=json&token={Uri.EscapeDataString(created.Key)}");

        viaHeader.Succeeded.Should().BeTrue();
        viaToken.Succeeded.Should().BeTrue("an API key is accepted wherever a token is");
        var principal = viaToken.Principal!;
        principal.Identity!.AuthenticationType.Should().Be(AuthenticationExtensions.ApiKeyScheme);
        principal.FindFirst("auth_type")!.Value.Should().Be("admin-api-key");
        principal.FindFirst("api_key_id")!.Value.Should().Be(created.Record.Id.ToString("D"));
        ClaimSet(principal).Should().BeEquivalentTo(ClaimSet(viaHeader.Principal!));
        principal.IsInRole(AdminOnlyRole).Should().BeFalse("a scoped key never gains the admin role");

        var context = new DefaultHttpContext { User = principal };
        AuditContextResolver.ResolveActor(context, out var actorType);
        actorType.Should().Be(AuditActorType.ApiKey);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task TokenPresentedBootstrapKey_ViaEsriAuthorizationHeader_YieldsTheXApiKeyPrincipal()
    {
        var dependencies = CreateDependencies(new InMemoryAdminApiKeyStore());

        var viaHeader = await AuthenticateViaXApiKeyAsync(dependencies, AdminPassword);
        var viaToken = await AuthenticateViaTokenAsync(
            dependencies,
            "?f=json",
            context => context.Request.Headers["X-Esri-Authorization"] = $"Bearer {AdminPassword}");

        viaToken.Succeeded.Should().BeTrue("an API key is accepted wherever a token is");
        viaToken.Principal!.Identity!.AuthenticationType.Should().Be(AuthenticationExtensions.ApiKeyScheme);
        viaToken.Principal.FindFirst("auth_type")!.Value.Should().Be("admin");
        ClaimSet(viaToken.Principal).Should().BeEquivalentTo(ClaimSet(viaHeader.Principal!));
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task TokenMatchingNeitherPortalTokenNorApiKey_FailsAsInvalidToken()
    {
        var dependencies = CreateDependencies(new InMemoryAdminApiKeyStore());

        var result = await AuthenticateViaTokenAsync(dependencies, "?f=json&token=not-a-token-or-key");

        result.Succeeded.Should().BeFalse();
        result.Failure.Should().NotBeNull();
    }

    private const string AdminOnlyRole = "admin";

    private static ApiKeyAuthenticationDependencies CreateDependencies(IAdminApiKeyStore store)
        => new(
            Options.Create(new ApiKeyAuthenticationOptions
            {
                EnvironmentName = "Production",
                AdminPassword = AdminPassword,
            }),
            secretResolver: null,
            adminApiKeyStore: store);

    private static async Task<AuthenticateResult> AuthenticateViaXApiKeyAsync(
        ApiKeyAuthenticationDependencies dependencies,
        string key)
    {
        var handler = new ApiKeyAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, dependencies);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-API-Key"] = key;
        await handler.InitializeAsync(
            new AuthenticationScheme(AuthenticationExtensions.ApiKeyScheme, null, typeof(ApiKeyAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static async Task<AuthenticateResult> AuthenticateViaTokenAsync(
        ApiKeyAuthenticationDependencies dependencies,
        string queryString,
        Action<HttpContext>? configure = null)
    {
        // No portal token was ever issued, so the issuer refuses every presented value.
        var issuer = Substitute.For<IPortalTokenIssuer>();
        issuer.ValidateAsync(Arg.Any<string>(), Arg.Any<PortalTokenBinding>(), Arg.Any<CancellationToken>())
            .Returns((PortalTokenValidation?)null);
        var handler = new PortalTokenAuthenticationHandler(Schemes(), NullLoggerFactory.Instance, UrlEncoder.Default, issuer);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(dependencies).BuildServiceProvider(),
        };
        context.Request.Path = "/sharing/rest/portals/self";
        context.Request.QueryString = new QueryString(queryString);
        configure?.Invoke(context);
        await handler.InitializeAsync(
            new AuthenticationScheme(PortalTokenAuthenticationExtensions.PortalTokenScheme, null, typeof(PortalTokenAuthenticationHandler)),
            context);
        return await handler.AuthenticateAsync();
    }

    private static IOptionsMonitor<AuthenticationSchemeOptions> Schemes()
    {
        var schemes = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        schemes.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        return schemes;
    }

    private static IEnumerable<string> ClaimSet(ClaimsPrincipal principal)
        => principal.Claims.Select(claim => $"{claim.Type}={claim.Value}");
}
