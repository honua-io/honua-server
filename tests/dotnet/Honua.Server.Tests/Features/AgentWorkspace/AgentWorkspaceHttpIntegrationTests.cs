// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.AgentWorkspace;

/// <summary>
/// Actual Kestrel requests, production API-key/JWT authentication, Redis workspace state,
/// and the canonical operation dispatcher. No authentication or executor mocks.
/// Human bearer issuance is test setup through the production issuer, not a browser-login claim.
/// </summary>
[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Admin)]
public sealed class AgentWorkspaceHttpIntegrationTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/agent/target")]
    [Endpoint("POST /api/v1/agent/devops/authorize")]
    [Endpoint("POST /api/v1/agent/workspaces")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}")]
    [Endpoint("PUT /api/v1/agent/workspaces/{workspaceId}/context")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/turns")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/turns/{turnId}")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/conversation")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/conversation")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/drafts")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}")]
    [Endpoint("PATCH /api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}")]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}/submit")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/commands/{commandId}")]
    public async Task HumanAndSeparateAgentShareFrozenContext_WhileForeignCredentialsAndReplaysAreBounded()
    {
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", $"http-test-{Guid.NewGuid():N}");
            builder.UseSetting("AgentWorkspace:ReleaseId", "isolated-http-test");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "http-integration-only-signing-key-at-least-32-bytes");
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "true");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "false");
        });
        await fixture.InitializeAsync();
        try
        {
            using var bootstrap = fixture.CreateAdminClient();
            var agentKey = await IssueKey(bootstrap, "workspace-agent", ["admin:read", "admin:write", "read:*", "agent:workspace"]);
            var foreignKey = await IssueKey(bootstrap, "foreign-agent", ["admin:read", "admin:write", "read:*", "agent:workspace"]);
            // Trusted host configuration mirrors installer-issued identity binding, never a model argument.
            fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = $"apikey:api-key:{agentKey.Id}";
            var bearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "workspace-human" },
                 new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5));
            bearer.Should().NotBeNull();
            using var human = fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer!.Token));
            var otherTenantBearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "workspace-human" },
                 new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "another-tenant" }], DateTimeOffset.UtcNow.AddMinutes(5));
            otherTenantBearer.Should().NotBeNull();
            using var otherTenant = fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", otherTenantBearer!.Token));
            using var agent = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", agentKey.Material));
            using var foreign = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", foreignKey.Material));
            using var anonymous = fixture.CreateClient();
            anonymous.DefaultRequestHeaders.Remove("X-API-Key");
            anonymous.DefaultRequestHeaders.Authorization = null;

            var targetResponse = await human.GetAsync("/api/v1/agent/target");
            targetResponse.Headers.CacheControl?.NoStore.Should().BeTrue();
            var target = await Read(targetResponse, HttpStatusCode.OK);
            var created = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "tab-a", targetId = target.GetProperty("targetId").GetString() }), HttpStatusCode.OK);
            string root = $"/api/v1/agent/workspaces/{created.GetProperty("workspaceId").GetString()}";
            string targetId = created.GetProperty("targetId").GetString()!;
            (await agent.GetAsync(root + "?viewId=tab-a")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await foreign.GetAsync(root + "?viewId=tab-a")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await otherTenant.GetAsync(root + "?viewId=tab-a")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await anonymous.GetAsync(root + "?viewId=tab-a")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await human.PostAsJsonAsync("/api/v1/agent/devops/authorize", new { targetId, deployTargetId = "not-configured", environment = "dev", canonicalTool = "arbitrary-tool" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var context = await Read(await human.PutAsJsonAsync(root + "/context", new { viewId = "tab-a", expectedRevision = 0, targetId, selectedResources = Array.Empty<object>(), task = (object?)null }), HttpStatusCode.OK);
            var draft = await Read(await human.PostAsJsonAsync(root + "/drafts", new
            {
                kind = "publish-layer", viewId = "tab-a", contextRevision = context.GetProperty("revision").GetInt64(),
                fields = new { connectionId = Guid.NewGuid().ToString(), schema = "public", table = "isolated_fixture", serviceName = "http-review", layerName = "initial" },
            }), HttpStatusCode.OK);
            string draftPath = root + "/drafts/" + draft.GetProperty("id").GetString();
            var edited = await Read(await agent.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 0, changes = new { layerName = "reviewed" } }), HttpStatusCode.OK);
            (await human.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 0, changes = new { layerName = "stale" } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await agent.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 1, changes = new { password = "must-not-be-shared" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var captured = await Read(await human.GetAsync(root + "?viewId=tab-a"), HttpStatusCode.OK);
            long capturedRevision = captured.GetProperty("revision").GetInt64();
            var turnRequest = new { turnId = "turn-one", viewId = "tab-a", contextRevision = capturedRevision, prompt = "Review this exact draft" };
            await Read(await human.PostAsJsonAsync(root + "/turns", turnRequest), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", turnRequest), HttpStatusCode.OK);
            (await human.PostAsJsonAsync(root + "/turns", new { turnId = "turn-one", viewId = "tab-a", contextRevision = capturedRevision, prompt = "Different request" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            await Read(await human.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 1, changes = new { layerName = "later-navigation" } }), HttpStatusCode.OK);
            var frozen = await Read(await agent.GetAsync(root + "/turns/turn-one"), HttpStatusCode.OK);
            frozen.GetProperty("context").GetProperty("revision").GetInt64().Should().Be(capturedRevision);
            frozen.GetProperty("draft").GetProperty("fields").GetProperty("layerName").GetString().Should().Be("reviewed");
            (await human.PutAsJsonAsync(root + "/context", new { viewId = "tab-a", expectedRevision = capturedRevision, targetId, selectedResources = Array.Empty<object>(), task = (object?)null })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            var currentDraft = await Read(await agent.GetAsync(draftPath), HttpStatusCode.OK);
            currentDraft.GetProperty("fields").GetProperty("layerName").GetString().Should().Be("later-navigation");
            (await human.PutAsJsonAsync(root + "/context", new
            {
                viewId = "tab-a", expectedRevision = capturedRevision + 1, targetId,
                selectedResources = new[] { new { kind = "layer", id = "2147483647", serviceId = "missing-service" } }, task = (object?)null,
            })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var message = new { messageId = "message-one", turnId = "turn-one", content = "This is conversation, not verified execution." };
            (await human.PostAsJsonAsync(root + "/conversation", message)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var assistant = await Read(await agent.PostAsJsonAsync(root + "/conversation", message), HttpStatusCode.OK);
            assistant.GetProperty("evidenceKind").GetString().Should().Be("conversation");
            await Read(await agent.PostAsJsonAsync(root + "/conversation", message), HttpStatusCode.OK);
            var conversation = await Read(await human.GetAsync(root + "/conversation"), HttpStatusCode.OK);
            conversation.GetProperty("messages").EnumerateArray().Count(value => value.GetProperty("id").GetString() == "message-one").Should().Be(1);
            (await foreign.GetAsync(root + "/conversation")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await agent.PostAsJsonAsync("/api/v1/admin/api-keys", new { name = "forbidden-escalation", permissions = new[] { "admin:write" } })).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // The gate creates a durable canonical proposal; no source table or publish executor runs.
            var submit = new { commandId = "command-one", expectedRevision = edited.GetProperty("revision").GetInt64() + 1, viewId = "tab-a" };
            var first = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            string instanceId = first.GetProperty("data").GetProperty("operationInstanceId").GetString()!;
            string proposalId = first.GetProperty("data").GetProperty("proposalId").GetString()!;
            proposalId.Should().NotBeNullOrWhiteSpace();
            (await agent.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "Agent may not approve its own proposal" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var replay = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            replay.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            var receipt = await Read(await agent.GetAsync(root + "/commands/command-one"), HttpStatusCode.OK);
            receipt.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            (await human.PostAsJsonAsync(draftPath + "/submit", submit)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await foreign.GetAsync(root + "/commands/command-one")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await bootstrap.PostAsync($"/api/v1/admin/api-keys/{agentKey.Id}/revoke", null)).EnsureSuccessStatusCode();
            var revoked = await agent.GetAsync(root + "?viewId=tab-a");
            revoked.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static async Task<(string Id, string Material)> IssueKey(HttpClient admin, string name, string[] permissions)
    {
        var body = await Read(await admin.PostAsJsonAsync("/api/v1/admin/api-keys", new { name, permissions }), HttpStatusCode.Created);
        return (body.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!, body.GetProperty("data").GetProperty("key").GetString()!);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            string body = await response.Content.ReadAsStringAsync();
            // Avoid including request credentials in assertion messages.
            response.StatusCode.Should().Be(expected);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }
}
