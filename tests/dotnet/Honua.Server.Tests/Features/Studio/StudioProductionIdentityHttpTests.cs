// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Db.Postgres.Features.Studio;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Mixins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
public sealed class StudioProductionIdentityHttpTests
{
    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public async Task ProductionComposition_IssuedKeyPreservesStudioOwnerAndCanonicalWorkspaceActor()
    {
        var postgres = new PostgresFixture();
        var redis = new RedisFixture();
        await postgres.InitializeAsync();
        try
        {
            await redis.InitializeAsync();
            try
            {
                // Exercise Program's infrastructure composition, migrations and auth.
                // No ConfigureTestServices or manually composed authority ports.
                var bridgeKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
                var settings = WebAppFixturePostgresWiringMixin.BuildAppConfigurationDictionary(
                    postgres.ConnectionString,
                    new Dictionary<string, string?>
                    {
                        ["HONUA_SKIP_MIGRATIONS"] = "false",
                        ["HONUA_ADMIN_PASSWORD"] = WebAppFixture.SharedAdminPassword,
                        ["HONUA_DEV_AUTH"] = "false",
                        ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "false",
                        ["ConnectionStrings:redis"] = redis.ConnectionString,
                        ["Licensing:DevGrantEdition"] = "Pro",
                        ["Studio:EndUserAuthorization:Enabled"] = "true",
                        ["AgentWorkspace:DeploymentId"] = "identity-" + Guid.NewGuid().ToString("N"),
                        ["AgentWorkspace:ReleaseId"] = "production-identity-test",
                        ["Authentication:OperatorBearer:Enabled"] = "true",
                        ["Authentication:OperatorBearer:SigningKey"] = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
                        ["AgentWorkspace:LocalHumanBridge:Enabled"] = "true",
                        ["AgentWorkspace:LocalHumanBridge:Key"] = bridgeKey,
                        ["AgentWorkspace:LocalHumanBridge:TenantId"] = "public",
                    });
                await using var factory = ConfiguredWebApplicationFactory.Create(builder =>
                {
                    // Program makes eager provider/Redis decisions during startup.
                    foreach (var setting in settings)
                    {
                        if (setting.Value is not null) builder.UseSetting(setting.Key, setting.Value);
                    }
                    builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
                }, "Development");
                factory.UseKestrel(0);
                using var setup = factory.CreateClient();
                setup.DefaultRequestHeaders.Add("X-API-Key", WebAppFixture.SharedAdminPassword);
                using var scope = factory.Services.CreateScope();
                scope.ServiceProvider.GetRequiredService<IStudioPackageStore>().Should().BeOfType<PostgresStudioPackageStore>();
                scope.ServiceProvider.GetService<IConnectionMultiplexer>().Should().NotBeNull();
                scope.ServiceProvider.GetRequiredService<IAgentWorkspaceStore>().IsAvailable.Should().BeTrue();
                using var human = factory.CreateClient();
                human.DefaultRequestHeaders.Add("X-Honua-Local-Bridge", bridgeKey);
                var session = await ReadAsync(await human.PostAsJsonAsync("/api/v1/admin/auth/local-console-session",
                    new { subject = "production-studio-controller" }), HttpStatusCode.OK);
                human.DefaultRequestHeaders.Remove("X-Honua-Local-Bridge");
                human.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());

                using var anonymous = factory.CreateClient();
                (await anonymous.GetAsync("/api/v1/agent/target")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                var bootstrap = await ReadAsync(await setup.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
                bootstrap.GetProperty("principal").GetProperty("isHumanSession").GetBoolean().Should().BeFalse();

                var itemId = Guid.NewGuid();
                var issued = await ReadAsync(await setup.PostAsJsonAsync("/api/v1/admin/api-keys", new
                {
                    name = "production-studio-identity",
                    permissions = new[] { "admin:read", "agent:workspace", $"agent:studio-compose:{itemId:D}", $"agent:studio-propose-publication:{itemId:D}" },
                }), HttpStatusCode.Created);
                var keyId = issued.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
                var workspaceActor = "apikey:api-key:" + keyId;
                factory.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = workspaceActor;
                using var agent = factory.CreateClient();
                agent.DefaultRequestHeaders.Add("X-API-Key", issued.GetProperty("data").GetProperty("key").GetString());
                var target = await ReadAsync(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
                var humanPrincipal = target.GetProperty("principal");
                humanPrincipal.GetProperty("isHumanSession").GetBoolean().Should().BeTrue();
                humanPrincipal.GetProperty("authenticationKind").GetString().Should().Be("operator-bearer");
                var agentTarget = await ReadAsync(await agent.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
                var agentPrincipal = agentTarget.GetProperty("principal");
                agentTarget.GetProperty("targetId").GetString().Should().Be(target.GetProperty("targetId").GetString());
                agentPrincipal.GetProperty("actorId").GetString().Should().Be(workspaceActor);
                agentPrincipal.GetProperty("studioOwnerId").GetString().Should().Be(keyId);
                agentPrincipal.GetProperty("isHumanSession").GetBoolean().Should().BeFalse();
                agentPrincipal.GetProperty("authenticationKind").GetString().Should().Be("api-key");
                var workspace = await ReadAsync(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new
                {
                    viewId = "identity-view",
                    targetId = target.GetProperty("targetId").GetString(),
                }), HttpStatusCode.OK);
                var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
                await ReadAsync(await human.PostAsJsonAsync($"/api/v1/agent/workspaces/{workspaceId}/turns", new
                {
                    turnId = "identity-turn",
                    viewId = "identity-view",
                    contextRevision = workspace.GetProperty("revision").GetInt64(),
                    prompt = "Compose the allocated Studio item and propose its publication.",
                }), HttpStatusCode.OK);
                var retained = await scope.ServiceProvider.GetRequiredService<IAgentWorkspaceStore>().GetAsync(workspaceId);
                retained!.AgentActorId.Should().Be(workspaceActor);
                retained.OwnerId.Should().Be(humanPrincipal.GetProperty("actorId").GetString());
                agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
                agent.DefaultRequestHeaders.Add("X-Honua-Turn", "identity-turn");
                await ReadAsync(await agent.GetAsync($"/api/v1/agent/workspaces/{workspaceId}/turns/identity-turn"), HttpStatusCode.OK);

                var packageKey = "identity-" + itemId.ToString("N");
                var created = await ReadAsync(await agent.PostAsJsonAsync("/api/v1/studio/package-drafts", new
                {
                    itemId,
                    packageKey,
                    envelope = new
                    {
                        family = "map",
                        schemaVersion = "1.0",
                        body = new { mapPackageId = packageKey, format = "honua_map_package.v1" },
                    },
                }), HttpStatusCode.Created);
                var draft = created.GetProperty("data");
                draft.GetProperty("ownerId").GetString().Should().Be(keyId);
                draft.GetProperty("itemId").GetGuid().Should().Be(itemId);
                var draftId = draft.GetProperty("draftId").GetGuid();
                var saved = await ReadAsync(await agent.PostAsJsonAsync($"/api/v1/studio/package-drafts/{draftId:D}/content-versions",
                    new { changeNote = "Production identity contract" }), HttpStatusCode.Created);
                var versionId = saved.GetProperty("data").GetProperty("versionId").GetGuid();
                var publication = await ReadAsync(await agent.PostAsJsonAsync(
                    $"/api/v1/studio/content-items/{itemId:D}/versions/{versionId:D}/publish-requests",
                    new { intent = new { route = "/studio/" + packageKey, visibility = "personal" } }), HttpStatusCode.Accepted);
                var proposalId = publication.GetProperty("data").GetProperty("proposalId").GetString();
                var proposal = await ReadAsync(await agent.GetAsync("/api/v1/admin/proposals/" + proposalId), HttpStatusCode.OK);
                proposal.GetProperty("requestedBy").GetString().Should().Be(keyId);
                proposal.GetProperty("status").GetString().Should().Be("AwaitingApproval");
                (await scope.ServiceProvider.GetRequiredService<IStudioPackageLifecycleService>().GetPointersAsync(itemId))!
                    .PublishedVersionId.Should().BeNull();
            }
            finally
            {
                await redis.DisposeAsync();
            }
        }
        finally
        {
            await postgres.DisposeAsync();
        }
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var payload = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(expected, "the production HTTP operation must succeed");
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.Clone();
        }
    }
}
