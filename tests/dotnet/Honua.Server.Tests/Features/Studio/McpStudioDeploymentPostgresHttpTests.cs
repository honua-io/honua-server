// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Deployment.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Publishing.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Db.Postgres.Features.Publishing;
using Honua.Db.Postgres.Features.Studio;
using Honua.Server.Tests.Features.Admin;
using Honua.TestKit;
using Honua.TestKit.Extensions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace Honua.Server.Tests.Features.Studio;

/// <summary>
/// An actual Studio MCP publication creates the canonical deployment read by a new
/// MCP request, and a rebuilt host reads the same row from PostgreSQL.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStudioDeploymentPostgresHttpTests : IAsyncLifetime
{
    private const string Issuer = AdminAuthEndpointsTests.TestOidcIssuer;
    private const string Audience = AdminAuthEndpointsTests.TestOidcAudience;
    private const string SigningKey = "studio-deployment-signing-key-at-least-32-characters";
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    private readonly WebAppFixture _fixture = new WebAppFixture().WithProductionJobSubstrate();
    private readonly RedisFixture _redis = new();
    private readonly AdminAuthEndpointsTests.StubHttpClientFactory _identityProvider = AdminAuthEndpointsTests.CreateStubFactory();

    public async Task InitializeAsync()
    {
        await _redis.InitializeAsync();
        HumanApprovalHttpClient.Configure(_fixture, _identityProvider).ConfigureWebHost(builder =>
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
        });
        _fixture.ConfigureServices(services =>
        {
            // TestKit re-registers the real Postgres provider after Program's feature
            // composition but leaves Database:Schema unset. Bind the production stores
            // to this fixture's retained schema before constructing MCP handlers.
            var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
            services.RemoveAll<IStudioPackageStore>();
            services.AddScoped<IStudioPackageStore>(provider => new PostgresStudioPackageStore(
                provider.GetRequiredService<IAdoNetDatabaseConnectionProvider>(), schema));
            services.RemoveAll<IDeploymentStore>();
            services.AddSingleton<IDeploymentStore>(provider => new PostgresDeploymentStore(
                provider.GetRequiredService<NpgsqlDataSource>(), schema));
            services.RemoveAll<IPublishedServiceStore>();
            services.AddSingleton<IPublishedServiceStore>(provider => new PostgresPublishedServiceStore(
                provider.GetRequiredService<NpgsqlDataSource>(), schema));
            services.AddMcpPromotionSurface();
        });
        await _fixture.InitializeAsync();
        using var scope = _fixture.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<IStudioPackageStore>()
            .Should().BeOfType<PostgresStudioPackageStore>();
        _fixture.Services.GetRequiredService<IDeploymentStore>()
            .Should().BeOfType<PostgresDeploymentStore>();
        await StudioPostgresTestSchema.ApplyAsync(_fixture);
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
        await _redis.DisposeAsync();
        _identityProvider.Dispose();
    }

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp tools/call honua_studio_propose_publication")]
    [Endpoint("POST /mcp resources/read honua://deployments/{deploymentId}")]
    public async Task PublishedMap_ExactReturnedDeploymentResourceSurvivesHostRestart()
    {
        using var admin = CreateTenantAdminClient("studio-alice", TenantA);
        var packageKey = $"deployment-map-{Guid.NewGuid():N}";
        var route = $"/maps/{packageKey}";
        var created = await CallToolAsync(admin, "honua_studio_create_draft",
            $$"""{"packageKey":"{{packageKey}}","family":"map","schemaVersion":"1.0"}""");
        var draftId = created.GetProperty("draftId").GetGuid();
        var updated = await CallToolAsync(admin, "honua_studio_update_draft",
            $$$"""{"body":{"mapPackageId":"{{{packageKey}}}","format":"honua_map_package.v1","status":"Ready","createdAt":"2026-01-01T00:00:00Z","title":"Roads Overview","description":"Road coverage map.","layers":[{"id":"roads","title":"Roads"}],"basemap":"streets"},"draftId":"{{{draftId:D}}}","generation":1,"packageKey":"{{{packageKey}}}","schemaVersion":"1.0"}""");
        var generation = updated.GetProperty("generation").GetInt64();
        await CallToolAsync(admin, "honua_studio_validate_draft",
            $$"""{"draftId":"{{draftId:D}}"}""");
        var saved = await CallToolAsync(admin, "honua_studio_save_version",
            $$"""{"draftId":"{{draftId:D}}","generation":{{generation}}}""");
        var version = saved.GetProperty("version");
        var itemId = version.GetProperty("itemId").GetGuid();
        var versionId = version.GetProperty("versionId").GetGuid();
        var contentHash = version.GetProperty("contentHash").GetString();

        var published = await CallToolAsync(admin, "honua_studio_propose_publication",
            $$"""{"itemId":"{{itemId:D}}","versionId":"{{versionId:D}}","contentHash":"{{contentHash}}","route":"{{route}}","visibility":"public"}""");
        var publication = await ApprovePublicationAsync(published, itemId, route);
        var deploymentId = publication.DeploymentId;
        var resourceUri = publication.DeploymentResourceUri;
        deploymentId.Should().NotBeNullOrWhiteSpace();
        resourceUri.Should().Be($"honua://deployments/{deploymentId}");

        var firstRead = await ReadDeploymentAsync(admin, resourceUri!);
        AssertDeployment(firstRead, deploymentId!, itemId, versionId, contentHash!);
        await AssertPersistedRowAsync(deploymentId!);

        await _fixture.RestartHostAsync();
        using var restartedAdmin = CreateTenantAdminClient("studio-alice", TenantA);
        var restartedRead = await ReadDeploymentAsync(restartedAdmin, resourceUri!);
        AssertDeployment(restartedRead, deploymentId!, itemId, versionId, contentHash!);
        restartedRead.GetProperty("etag").GetString().Should().Be(firstRead.GetProperty("etag").GetString());

        // The exact URI remains present after restart, but a different tenant's
        // admin must not learn its source identity or deployment state.
        using var otherTenant = CreateTenantAdminClient("studio-bob", TenantB);
        using var crossTenant = await PostRpcAsync(otherTenant,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "other-tenant", method = "resources/read", @params = new { uri = resourceUri } }));
        var crossTenantPayload = await crossTenant.Content.ReadAsStringAsync();
        if (crossTenant.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(crossTenantPayload);
            document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
                .GetString().Should().Be("not_found");
        }
        else
        {
            crossTenant.StatusCode.Should().Be(HttpStatusCode.NotFound, crossTenantPayload);
        }

        using var anonymous = _fixture.CreateClient();
        using var denied = await PostRpcAsync(anonymous,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "anon", method = "resources/read", @params = new { uri = resourceUri } }));
        var denial = await denied.Content.ReadAsStringAsync();
        if (denied.StatusCode == HttpStatusCode.OK)
        {
            using var document = JsonDocument.Parse(denial);
            document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
                .GetString().Should().Be("unauthenticated");
        }
        else
        {
            denied.StatusCode.Should().Be(HttpStatusCode.Unauthorized, denial);
        }
    }

    private async Task AssertPersistedRowAsync(string deploymentId)
    {
        var schema = _fixture.CurrentSchema ?? throw new InvalidOperationException("Missing fixture schema.");
        await using var connection = await _fixture.Postgres.GetConnectionAsync(schema);
        await using var command = new NpgsqlCommand(
            "SELECT source_kind FROM honua.promotion_deployments WHERE deployment_id = @deployment_id",
            connection);
        command.Parameters.AddWithValue("@deployment_id", deploymentId);
        (await command.ExecuteScalarAsync()).Should().Be("StudioPublishedVersion");
    }

    private static void AssertDeployment(JsonElement readback, string deploymentId, Guid itemId,
        Guid versionId, string contentHash)
    {
        readback.GetProperty("deploymentId").GetString().Should().Be(deploymentId);
        readback.GetProperty("sourceKind").GetString().Should().Be("StudioPublishedVersion");
        readback.GetProperty("studioItemId").GetGuid().Should().Be(itemId);
        readback.GetProperty("studioVersionId").GetGuid().Should().Be(versionId);
        readback.GetProperty("studioContentHash").GetString().Should().Be(contentHash);
        readback.GetProperty("status").GetString().Should().Be("Active");
        readback.GetProperty("runtimeHealth").GetString().Should().Be("Unknown");
    }

    private static async Task<JsonElement> ReadDeploymentAsync(HttpClient client, string uri)
    {
        var response = await RpcAsync(client,
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "read", method = "resources/read", @params = new { uri } }));
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        var contents = response.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);
        contents[0].GetProperty("uri").GetString().Should().Be(uri);
        using var document = JsonDocument.Parse(contents[0].GetProperty("text").GetString()!);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> CallToolAsync(HttpClient client, string name, string arguments)
    {
        var response = await RpcAsync(client,
            $$$"""{"jsonrpc":"2.0","id":"{{{name}}}","method":"tools/call","params":{"name":"{{{name}}}","arguments":{{{arguments}}}}}""");
        response.TryGetProperty("error", out _).Should().BeFalse(response.GetRawText());
        var result = response.GetProperty("result");
        result.TryGetProperty("isError", out var isError).Should().BeTrue(result.GetRawText());
        isError.GetBoolean().Should().BeFalse(result.GetRawText());
        return result.GetProperty("structuredContent").Clone();
    }

    private static async Task<JsonElement> RpcAsync(HttpClient client, string body)
    {
        using var response = await PostRpcAsync(client, body);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PostRpcAsync(HttpClient client, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        };
        return await client.SendAsync(request);
    }

    private HttpClient CreateTenantAdminClient(string subject, string tenantId)
    {
        var client = _fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken(subject, tenantId));
        return client;
    }

    private static string CreateToken(string subject, string tenantId)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim("sub", subject),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
                new Claim("tid", tenantId),
                new Claim("roles", "admin"),
                new Claim("scope", OperatorScopeCatalog.Full),
            ],
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
    private async Task<StudioPublicationRequest> ApprovePublicationAsync(JsonElement published, Guid itemId, string route)
    {
        published.GetProperty("status").GetString().Should().Be("AwaitingApproval");
        published.TryGetProperty("shareUrl", out _).Should().BeFalse();
        using var approvalScope = _fixture.Services.CreateScope();
        var lifecycle = approvalScope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>();
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().BeNull();
        using var approver = await HumanApprovalHttpClient.AuthenticateAsync(
            _fixture, _identityProvider, "deployment-human-publisher", TenantA);
        using var approval = await approver.ApproveAsync(published.GetProperty("proposalId").GetString()!);
        var approvalBody = await approval.Content.ReadAsStringAsync();
        approval.StatusCode.Should().Be(HttpStatusCode.OK, approvalBody);
        using var decision = JsonDocument.Parse(approvalBody);
        decision.RootElement.GetProperty("status").GetString().Should().Be("Succeeded", approvalBody);
        var publication = await lifecycle.GetActivePublicationRequestByRouteAsync(route);
        publication.Should().NotBeNull();
        publication!.Status.Should().Be(StudioPublicationRequestStatus.Accepted);
        publication.PublicationUrl.Should().Be(StudioPublishedRoutes.BuildActiveUrl(route));
        return publication;
    }

}
