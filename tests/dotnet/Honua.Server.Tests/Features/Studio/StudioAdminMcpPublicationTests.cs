// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// An admin API key publishing a saved Studio version from
/// <c>honua_studio_propose_publication</c> receives the active share URL in that
/// call. A caller who is not admin still gets a proposal and no share URL
/// (honua-server#5207). Licensing is disabled, the 2026.1 posture in which a
/// publication request would otherwise wait for approval.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class StudioAdminMcpPublicationTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string Issuer = "https://idp.example.com";
    private const string Audience = "honua-mcp-client-id";
    private const string SigningKey = "studio-admin-publication-signing-key-at-least-32-chars";
    private const string Tenant = "tenant-a";

    private readonly WebAppFixture _fixture = new();
    private readonly RedisFixture _redis = new();

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        _fixture
            .ConfigureWebHost(builder =>
            {
                builder.UseEnvironment("Test");
                builder.UseSetting("ConnectionStrings:redis", _redis.ConnectionString);
                builder.UseSetting("Licensing:Mode", "Disabled");
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
                builder.UseSetting("Studio:EndUserAuthorization:Enabled", "true");
                builder.UseSetting("Oidc:Enabled", "true");
                builder.UseSetting("Oidc:RequireHttps", "true");
                builder.UseSetting("Oidc:TokenValidation:SymmetricSigningKey", SigningKey);
                builder.UseSetting("Oidc:TokenValidation:EnableTokenReplayProtection", "false");
                builder.UseSetting("Oidc:Generic:Enabled", "true");
                builder.UseSetting("Oidc:Generic:Authority", Issuer);
                builder.UseSetting("Oidc:Generic:ClientId", Audience);
                builder.UseSetting("Oidc:Generic:DisplayName", "Test IdP");
            })
            .ConfigureServices(services =>
            {
                services.RemoveAll<IStudioPackageStore>();
                services.AddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
                services.RemoveAll<IOperatorAuthorizationEvaluator>();
                services.AddSingleton<IOperatorAuthorizationEvaluator, AllowAllOperatorAuthorizationEvaluator>();
            });
        await _fixture.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("GET /api/v1/studio/published/{*route}")]
    public async Task AdminKey_ProposePublication_PublishesAndReturnsTheActiveRoute()
    {
        using var admin = _fixture.CreateAdminClient();
        var route = $"/maps/admin-{Guid.NewGuid():N}";

        var (proposed, itemId) = await ProposeAsync(admin, route, "public");

        proposed.GetProperty("status").GetString().Should().Be("Published");
        proposed.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeFalse();
        proposed.TryGetProperty("proposalId", out _).Should().BeFalse();
        var shareUrl = proposed.GetProperty("shareUrl").GetString();
        shareUrl.Should().Be(StudioPublishedRoutes.BuildActiveUrl(route));

        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().NotBeNull();

        using var anonymous = _fixture.CreateClient();
        using var published = await anonymous.GetAsync(shareUrl);
        published.StatusCode.Should().Be(HttpStatusCode.OK, await published.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    public async Task NonAdmin_ProposePublication_AwaitsApprovalWithoutShareUrl()
    {
        using var caller = CreateBearerClient(CreateToken("studio-member-without-admin"));
        var route = $"/maps/member-{Guid.NewGuid():N}";

        var (proposed, itemId) = await ProposeAsync(caller, route, "public");

        proposed.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        proposed.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeTrue();
        proposed.GetProperty("proposalId").GetString().Should().NotBeNullOrWhiteSpace();
        proposed.TryGetProperty("shareUrl", out _).Should().BeFalse();

        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().BeNull();

        using var anonymous = _fixture.CreateClient();
        using var unpublished = await anonymous.GetAsync(StudioPublishedRoutes.BuildActiveUrl(route));
        unpublished.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<(JsonElement Proposed, Guid ItemId)> ProposeAsync(HttpClient client, string route, string visibility)
    {
        var packageKey = $"admin-publish-{Guid.NewGuid():N}";
        var created = await CallToolAsync(client, "honua_studio_create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"dashboard","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();

        var updated = await CallToolAsync(client, "honua_studio_update_draft",
            $$$"""{"body":{"layers":[{"id":"roads"}],"view":{"center":[-158,22],"zoom":7}},"draftId":"{{{draftId:D}}}","generation":1,"packageKey":"{{{packageKey}}}","schemaVersion":"1.0"}""");
        var generation = updated.GetProperty("generation").GetInt64();

        await CallToolAsync(client, "honua_studio_validate_draft", $$"""{"draftId":"{{draftId:D}}"}""");

        var saved = await CallToolAsync(client, "honua_studio_save_version",
            $$"""{"draftId":"{{draftId:D}}","generation":{{generation}}}""");
        var version = saved.GetProperty("version");

        var itemId = version.GetProperty("itemId").GetGuid();
        var proposed = await CallToolAsync(client, "honua_studio_propose_publication",
            $$"""{"itemId":"{{itemId:D}}","versionId":"{{version.GetProperty("versionId").GetGuid():D}}","contentHash":"{{version.GetProperty("contentHash").GetString()}}","route":"{{route}}","visibility":"{{visibility}}"}""");
        return (proposed, itemId);
    }

    private HttpClient CreateBearerClient(string token)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string toolName, string argumentsJson)
    {
        var response = await RpcAsync(
            client,
            $$$"""{"jsonrpc":"2.0","id":"{{{toolName}}}","method":"tools/call","params":{"name":"{{{toolName}}}","arguments":{{{argumentsJson}}}}}""");
        var hasProtocolError = response.TryGetProperty("error", out var protocolError);
        hasProtocolError.Should().BeFalse(
            $"tool '{toolName}' should return a tool result: {(hasProtocolError ? protocolError.GetRawText() : "(none)")}");
        var result = response.GetProperty("result");
        if (result.TryGetProperty("isError", out var isError))
        {
            isError.GetBoolean().Should().BeFalse($"tool '{toolName}' failed: {result.GetRawText()}");
        }

        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue(JsonMediaType) } },
        };
        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static string CreateToken(string subject)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("tid", Tenant),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class AllowAllOperatorAuthorizationEvaluator : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AccessDecision.Allowed("admin-publication fixture"));
    }
}
