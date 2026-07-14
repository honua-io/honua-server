// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Sharing;

/// <summary>
/// End-to-end conformance for the ArcGIS Portal/Sharing facade discovery flow that
/// packaged Esri clients (ArcGIS Pro "Add Portal", Field Maps) drive (epic #1240,
/// child #1372). Where <see cref="SharingRestReadTests"/> proves the anonymous read
/// surface and <see cref="SharingRestTokenTests"/> proves token issuance, this suite
/// closes the two coverage gaps the epic acceptance criteria call out:
/// <list type="number">
/// <item>
/// the full <c>public</c>/<c>org</c>/<c>private</c> RBAC-projected access ladder — the
/// existing read tests only exercise <c>public</c> and role-restricted items
/// <em>anonymously</em>, never the <c>org</c> tier nor a token-authenticated caller; and
/// </item>
/// <item>
/// the exact Esri wire shapes a real client is strict about: <c>generateToken</c>'s
/// <c>expires</c> as Unix <em>milliseconds</em> (not seconds), and the
/// <c>portals/self</c>/<c>community/self</c> user blocks that only appear once a token
/// authenticates the request.
/// </item>
/// </list>
/// The access tier is a projection of the shared RBAC evaluator, so the fixture uses a
/// <c>portal-admin</c>-restricted private service to prove the admin-issued token sees
/// <c>public</c> + <c>org</c> content but is still denied the role-gated <c>private</c>
/// item — i.e. the Portal <c>access</c> string cannot drift from the real access decision.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class PortalFacadeDiscoveryContractTests : IAsyncLifetime
{
    private const string AdminPassword = WebAppFixture.SharedAdminPassword;
    private const string PublicServiceId = "portal-public-svc";
    private const string OrgServiceId = "portal-org-svc";
    private const string PrivateServiceId = "portal-private-svc";

    // The in-process WebApplicationFactory transport leaves Connection.RemoteIpAddress
    // unset. IP-bound token issuance (client=requestip) needs it, so the fixture stamps a
    // fixed loopback IP onto every request exactly as SharingRestTokenTests does.
    private static readonly IPAddress ClientIp = IPAddress.Parse("203.0.113.20");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly WebAppFixture _fixture;

    public PortalFacadeDiscoveryContractTests()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddService(
                PublicServiceId,
                "Public Basemap",
                protocols: [ServiceProtocols.FeatureServer],
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddService(
                OrgServiceId,
                "Org Operational Layers",
                protocols: [ServiceProtocols.FeatureServer],
                // No AllowAnonymous, no role restriction -> any authenticated principal
                // is allowed, which the projector maps to the "org" access tier.
                accessPolicy: new AccessPolicy())
            .AddService(
                PrivateServiceId,
                "Restricted Parcels",
                protocols: [ServiceProtocols.FeatureServer],
                // Role-gated: the admin token (role "admin") must NOT satisfy this,
                // proving the "private" tier and that access cannot drift from RBAC.
                accessPolicy: new AccessPolicy { AllowedRoles = ["portal-admin"] })
            .Build();

        _fixture = new WebAppFixture()
            .ReplaceService<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph))
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
                // Allow the in-process HTTP transport to issue and accept tokens; the
                // production RequireHttps=true default is unaffected.
                builder.UseSetting("Authentication:PortalToken:RequireHttps", "false");
                builder.ConfigureServices(services =>
                    services.AddSingleton<IStartupFilter>(new RemoteIpStartupFilter(ClientIp)));
            });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task Search_Anonymous_ProjectsOnlyPublicTier()
    {
        using var client = _fixture.CreateClient();
        using var response = await client.GetAsync("/sharing/rest/search?f=json&num=25");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadSearchAsync(response);

        payload.Results.Should().ContainSingle();
        payload.Results[0].Id.Should().Be(PublicServiceId);
        payload.Results[0].Access.Should().Be("public");
        // Org and private tiers must never surface to an anonymous caller.
        payload.Results.Should().NotContain(r => r.Id == OrgServiceId);
        payload.Results.Should().NotContain(r => r.Id == PrivateServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/search")]
    public async Task Search_WithNamedUserToken_ProjectsPublicAndOrgTiers_NotRoleGatedPrivate()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueAdminTokenAsync(client);

        using var response = await client.GetAsync($"/sharing/rest/search?f=json&num=25&token={token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadSearchAsync(response);

        var byId = payload.Results.ToDictionary(r => r.Id, r => r.Access);
        // The org tier becomes visible once a named user authenticates, projected as "org".
        byId.Should().ContainKey(OrgServiceId).WhoseValue.Should().Be("org");
        byId.Should().ContainKey(PublicServiceId).WhoseValue.Should().Be("public");
        // The role-gated private item stays hidden: the admin token lacks "portal-admin",
        // so the Portal access projection cannot over-report visibility.
        byId.Should().NotContainKey(PrivateServiceId);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}")]
    public async Task ContentItem_OrgTier_AnonymousGets404_ButNamedUserRoundTrips()
    {
        using var client = _fixture.CreateClient();

        // Anonymous: the org item must not even confirm its existence.
        using var anonResponse = await client.GetAsync($"/sharing/rest/content/items/{OrgServiceId}?f=json");
        await anonResponse.AssertGeoServicesErrorAsync(404);

        // Named user: the same item resolves to a /rest/services FeatureServer URL,
        // tagged with the org access tier.
        var token = await IssueAdminTokenAsync(client);
        using var authResponse = await client.GetAsync($"/sharing/rest/content/items/{OrgServiceId}?f=json&token={token}");

        authResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await authResponse.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("id").GetString().Should().Be(OrgServiceId);
        root.GetProperty("access").GetString().Should().Be("org");
        root.GetProperty("type").GetString().Should().Be("Feature Service");
        var url = root.GetProperty("url").GetString();
        url.Should().Contain("/rest/services/");
        url.Should().EndWith("/FeatureServer");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/content/items/{id}")]
    public async Task ContentItem_RoleGatedPrivate_NamedUserWithoutRoleStillGets404()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueAdminTokenAsync(client);

        using var response = await client.GetAsync($"/sharing/rest/content/items/{PrivateServiceId}?f=json&token={token}");

        // Authenticated but unauthorized: the projector returns null -> 404, never a leak.
        await response.AssertGeoServicesErrorAsync(404);
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task PortalsSelf_WithNamedUserToken_PopulatesUserBlock()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueAdminTokenAsync(client);

        using var response = await client.GetAsync($"/sharing/rest/portals/self?f=json&token={token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // Fields ArcGIS Pro "Add Portal" reads to confirm a portal connection.
        root.GetProperty("isPortal").GetBoolean().Should().BeTrue();
        root.GetProperty("portalName").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("name").GetString().Should().NotBeNullOrWhiteSpace();

        root.TryGetProperty("user", out var user).Should().BeTrue();
        user.ValueKind.Should().Be(JsonValueKind.Object);
        user.GetProperty("username").GetString().Should().Be("admin");
        user.GetProperty("role").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /sharing/rest/community/self")]
    public async Task CommunitySelf_WithNamedUserToken_ReturnsUserDocument()
    {
        using var client = _fixture.CreateClient();
        var token = await IssueAdminTokenAsync(client);

        using var response = await client.GetAsync($"/sharing/rest/community/self?f=json&token={token}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("username").GetString().Should().Be("admin");
        root.GetProperty("role").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /sharing/rest/generateToken")]
    public async Task GenerateToken_EmitsExpiryAsUnixMilliseconds()
    {
        using var client = _fixture.CreateClient();
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "admin"),
            new KeyValuePair<string, string>("password", AdminPassword),
            new KeyValuePair<string, string>("client", "requestip"),
            new KeyValuePair<string, string>("f", "json"),
        });
        var issuedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var response = await client.PostAsync("/sharing/rest/generateToken", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await ReadTokenAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
        payload.Ssl.Should().BeTrue();

        // The canonical ArcGIS shape is Unix *milliseconds*. A client that reads this
        // as seconds (or a server that emits seconds) breaks Pro/Field Maps, so assert
        // the value is unambiguously a millisecond timestamp: it must be far larger than
        // the same instant expressed in seconds, and land within a day of "now".
        var nowSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        payload.Expires.Should().BeGreaterThan(issuedAtMs);
        payload.Expires.Should().BeGreaterThan(nowSeconds * 1000L,
            "the ArcGIS token 'expires' field is Unix milliseconds, not seconds");
        var expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(payload.Expires);
        (expiresAt - DateTimeOffset.UtcNow).Should().BeLessThan(TimeSpan.FromDays(1));
    }

    private async Task<string> IssueAdminTokenAsync(HttpClient client)
    {
        using var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", "admin"),
            new KeyValuePair<string, string>("password", AdminPassword),
            // IP-bound token (RemoteIpStartupFilter stamps a stable client IP) so the
            // follow-up read requests authenticate without carrying a referer each time.
            new KeyValuePair<string, string>("client", "requestip"),
            new KeyValuePair<string, string>("f", "json"),
        });
        using var response = await client.PostAsync("/sharing/rest/generateToken", content);
        response.EnsureSuccessStatusCode();
        var payload = await ReadTokenAsync(response);
        payload.Token.Should().NotBeNullOrWhiteSpace();
        return payload.Token;
    }

    private static async Task<SearchPayload> ReadSearchAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<SearchPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty search response body.");
    }

    private static async Task<TokenPayload> ReadTokenAsync(HttpResponseMessage response)
    {
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<TokenPayload>(json, JsonOptions)
            ?? throw new InvalidOperationException("Empty token response body.");
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

    private sealed record SearchPayload
    {
        public int Total { get; init; }

        public int Start { get; init; }

        public int Num { get; init; }

        public int NextStart { get; init; }

        public ItemPayload[] Results { get; init; } = Array.Empty<ItemPayload>();
    }

    private sealed record ItemPayload
    {
        public string Id { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string Access { get; init; } = string.Empty;
    }

    private sealed record TokenPayload
    {
        public string Token { get; init; } = string.Empty;

        public long Expires { get; init; }

        public bool Ssl { get; init; }
    }
}
