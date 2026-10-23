// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Server.Features.Operations;
using StackExchange.Redis;
using Honua.Server.Features.AgentWorkspace;
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
        var sourceTable = $"workspace_source_{Guid.NewGuid():N}";
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
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
        });
        await fixture.InitializeAsync();
        try
        {
            var sourceConnection = await fixture.GetTestSecureConnectionIdAsync();
            sourceConnection.Should().NotBeNull("the actual publish validation needs a registered source connection");
            await fixture.Postgres.ExecuteDdlUnderLockAsync($"CREATE TABLE public.{sourceTable} (id SERIAL PRIMARY KEY, name TEXT NOT NULL, geom geometry(Point,4326) NOT NULL); INSERT INTO public.{sourceTable} (name, geom) VALUES ('Original source', ST_SetSRID(ST_Point(1,1),4326));");
            using var bootstrap = fixture.CreateAdminClient();
            var agentKey = await IssueKey(bootstrap, "workspace-agent", ["admin:read", "agent:propose-publication", "agent:workspace"]);
            var foreignKey = await IssueKey(bootstrap, "foreign-agent", ["admin:read", "agent:propose-publication", "agent:workspace"]);
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
                kind = "publish-layer",
                viewId = "tab-a",
                contextRevision = context.GetProperty("revision").GetInt64(),
                fields = new { connectionId = sourceConnection!.Value.ToString(), schema = "public", table = sourceTable, serviceName = "http-review", layerName = "initial" },
            }), HttpStatusCode.OK);
            string draftPath = root + "/drafts/" + draft.GetProperty("id").GetString();
            var editContext = await Read(await human.GetAsync(root + "?viewId=tab-a"), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            {
                turnId = "turn-edit", viewId = "tab-a", contextRevision = editContext.GetProperty("revision").GetInt64(),
                prompt = "Review this initial draft"
            }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", created.GetProperty("workspaceId").GetString());
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-edit");
            var edited = await Read(await agent.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 0, changes = new { layerName = "reviewed" } }), HttpStatusCode.OK);
            (await human.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 0, changes = new { layerName = "stale" } })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await agent.PatchAsJsonAsync(draftPath, new { viewId = "tab-a", expectedRevision = 1, changes = new { password = "must-not-be-shared" } })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            agent.DefaultRequestHeaders.Remove("X-Honua-Workspace");
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
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
                viewId = "tab-a",
                expectedRevision = capturedRevision + 1,
                targetId,
                selectedResources = new[] { new { kind = "layer", id = "2147483647", serviceId = "missing-service" } },
                task = (object?)null,
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

            // Generic MCP publishing must share the durable inbox even with admin exemption enabled.
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", created.GetProperty("workspaceId").GetString());
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-one");
            var initialized = await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "workspace-publish-test", version = "1" } },
            });
            initialized.EnsureSuccessStatusCode();
            var mcpSession = initialized.Headers.GetValues("Mcp-Session-Id").Single();
            using var publishRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
            {
                Content = JsonContent.Create(new
                {
                    jsonrpc = "2.0",
                    id = 2,
                    method = "tools/call",
                    @params = new
                    {
                        name = "honua_publish_service",
                        arguments = new
                        {
                            connectionId = sourceConnection!.Value.ToString(),
                            schema = "public",
                            table = sourceTable,
                            layerName = "Generic_review",
                        }
                    },
                }),
            };
            publishRequest.Headers.Add("Mcp-Session-Id", mcpSession);
            var generic = await Read(await agent.SendAsync(publishRequest), HttpStatusCode.OK);
            var genericHandle = generic.GetProperty("result").GetProperty("structuredContent");
            genericHandle.GetProperty("status").GetString().Should().Be("RequiresApproval");
            var genericProposal = genericHandle.GetProperty("proposalId").GetString();
            genericProposal.Should().NotBeNullOrWhiteSpace();
            (await human.GetAsync($"/api/v1/admin/proposals/{genericProposal}")).StatusCode.Should().Be(HttpStatusCode.OK);
            genericHandle.GetProperty("auditId").GetString().Should().NotBeNullOrWhiteSpace();
            (await agent.PostAsJsonAsync($"/api/v1/admin/proposals/{genericProposal}/approve", new { reason = "Agent cannot approve generic publication" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            agent.DefaultRequestHeaders.Remove("X-Honua-Workspace");
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");

            // The gate creates a durable canonical proposal; no source table or publish executor runs.
            var submit = new { commandId = "command-one", expectedRevision = edited.GetProperty("revision").GetInt64() + 1, viewId = "tab-a" };
            // The retained old turn cannot silently adopt the human's revision2 edit.
            (await agent.PostAsJsonAsync(draftPath + "/submit", submit)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            var reviewContext = await Read(await human.GetAsync(root + "?viewId=tab-a"), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            {
                turnId = "turn-review",
                viewId = "tab-a",
                contextRevision = reviewContext.GetProperty("revision").GetInt64(),
                prompt = "Publish this newly reviewed revision"
            }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", created.GetProperty("workspaceId").GetString());
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-review");
            var first = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            string instanceId = first.GetProperty("data").GetProperty("operationInstanceId").GetString()!;
            string proposalId = first.GetProperty("data").GetProperty("proposalId").GetString()!;
            proposalId.Should().NotBeNullOrWhiteSpace();
            (await agent.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "Agent may not approve its own proposal" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var replay = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            replay.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            (await human.GetAsync(root + "/commands/command-one")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            var receipt = await Read(await agent.GetAsync(root + "/commands/command-one"), HttpStatusCode.OK);
            receipt.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            // Different tabs mint different command ids for one reviewed revision. They must
            // converge on the original operation, including after a Redis CAS collision.
            var concurrentCommands = new[] { "command-tab-b", "command-tab-c" };
            var concurrentResponses = await Task.WhenAll(concurrentCommands.Select(commandId =>
                agent.PostAsJsonAsync(draftPath + "/submit", new { commandId, submit.expectedRevision, viewId = "tab-a" })));
            foreach (var response in concurrentResponses)
            {
                using (response)
                {
                    response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
                }
            }
            foreach (var commandId in concurrentCommands)
            {
                var recovered = await Read(await agent.PostAsJsonAsync(draftPath + "/submit",
                    new { commandId, submit.expectedRevision, viewId = "tab-a" }), HttpStatusCode.OK);
                recovered.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
                var recoveredReceipt = await Read(await agent.GetAsync(root + "/commands/" + commandId), HttpStatusCode.OK);
                recoveredReceipt.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            }
            var persisted = await fixture.Services.GetRequiredService<IAgentWorkspaceStore>()
                .GetAsync(created.GetProperty("workspaceId").GetString()!);
            persisted!.Commands["command-one"].OriginatingTurnId.Should().Be("turn-review");
            persisted.Commands.Values.Where(command => command.DraftId == currentDraft.GetProperty("id").GetString())
                .Select(command => command.OperationInstanceId).Distinct().Should().ContainSingle();
            (await human.PostAsJsonAsync(draftPath + "/submit",
                new { commandId = "different-actor-command", submit.expectedRevision, viewId = "tab-a" }))
                .StatusCode.Should().Be(HttpStatusCode.Conflict);

            // A CAS loser can recover read-only even when its alias was never accepted.
            var recoveryQuery = $"?draftId={currentDraft.GetProperty("id").GetString()}&draftRevision={submit.expectedRevision}";
            var discovered = await Read(await agent.GetAsync(root + "/commands/unpersisted-tab" + recoveryQuery), HttpStatusCode.OK);
            discovered.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            (await agent.GetAsync(root + "/commands/unpersisted-tab")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await human.GetAsync(root + "/commands/unpersisted-tab" + recoveryQuery)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await agent.GetAsync(root + "/commands/command-one?draftId=other&draftRevision=0"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await agent.GetAsync(root + "/commands/unpersisted-tab?draftRevision=0"))
                .StatusCode.Should().Be(HttpStatusCode.BadRequest);
            var afterDiscovery = await fixture.Services.GetRequiredService<IAgentWorkspaceStore>()
                .GetAsync(created.GetProperty("workspaceId").GetString()!);
            afterDiscovery!.Version.Should().Be(persisted.Version);
            afterDiscovery.Commands.Should().NotContainKey("unpersisted-tab");

            // A deliberate retry is a newly reviewed revision, never a new command id
            // for the consumed revision. Empty PATCH still uses the canonical draft CAS.
            var retryDraft = await Read(await agent.PatchAsJsonAsync(draftPath,
                new { viewId = "tab-a", expectedRevision = submit.expectedRevision, changes = new { } }), HttpStatusCode.OK);
            var retryRevision = retryDraft.GetProperty("revision").GetInt64();
            retryRevision.Should().Be(submit.expectedRevision + 1);
            var freshCommands = new[] { "fresh-tab-a", "fresh-tab-b" };
            var freshResponses = await Task.WhenAll(freshCommands.Select(commandId => agent.PostAsJsonAsync(draftPath + "/submit",
                new { commandId, expectedRevision = retryRevision, viewId = "tab-a" })));
            foreach (var response in freshResponses)
            {
                using (response)
                {
                    response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Conflict);
                }
            }
            var freshInstances = new List<string>();
            foreach (var commandId in freshCommands)
            {
                var recovered = await Read(await agent.PostAsJsonAsync(draftPath + "/submit",
                    new { commandId, expectedRevision = retryRevision, viewId = "tab-a" }), HttpStatusCode.OK);
                freshInstances.Add(recovered.GetProperty("data").GetProperty("operationInstanceId").GetString()!);
            }
            freshInstances.Distinct().Should().ContainSingle().Which.Should().NotBe(instanceId);
            var laterHuman = await Read(await human.PatchAsJsonAsync(draftPath,
                new { viewId = "tab-a", expectedRevision = retryRevision, changes = new { layerName = "later-human-edit" } }), HttpStatusCode.OK);
            var laterRevision = laterHuman.GetProperty("revision").GetInt64();
            (await agent.PatchAsJsonAsync(draftPath, new
            {
                viewId = "tab-a",
                expectedRevision = laterRevision,
                changes = new { layerName = "must-not-adopt" }
            })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await agent.PostAsJsonAsync(draftPath + "/submit", new
            {
                commandId = "must-not-adopt",
                expectedRevision = laterRevision,
                viewId = "tab-a"
            })).StatusCode.Should().Be(HttpStatusCode.Conflict);
            // A known command and a same-revision alias observe the original receipt only.
            var knownAfterEdit = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            knownAfterEdit.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            var originalAfterEdit = await Read(await agent.PostAsJsonAsync(draftPath + "/submit",
                new { commandId = "old-revision-recovery", submit.expectedRevision, viewId = "tab-a" }), HttpStatusCode.OK);
            originalAfterEdit.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(instanceId);
            (await human.PostAsJsonAsync(draftPath + "/submit", submit)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await foreign.GetAsync(root + "/commands/command-one")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await bootstrap.PostAsync($"/api/v1/admin/api-keys/{agentKey.Id}/revoke", null)).EnsureSuccessStatusCode();
            var revoked = await agent.GetAsync(root + "?viewId=tab-a");
            revoked.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
        }
        finally
        {
            await fixture.Postgres.ExecuteDdlUnderLockAsync($"DROP TABLE IF EXISTS public.{sourceTable}");
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("POST /api/v1/agent/workspaces/{workspaceId}/drafts/{draftId}/submit")]
    [Endpoint("GET /api/v1/agent/workspaces/{workspaceId}/commands/{commandId}")]
    public async Task JournalCommitBeforeDispatchRecoversOriginalFieldsAndTurnWithoutConsumingUnauthorizedRevision()
    {
        var deployment = $"journal-crash-{Guid.NewGuid():N}";
        var sourceTable = $"workspace_source_{Guid.NewGuid():N}";
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", deployment);
            builder.UseSetting("AgentWorkspace:ReleaseId", "isolated-journal-crash-test");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "journal-crash-test-signing-key-at-least-32-bytes");
            builder.ConfigureServices(services => services.AddSingleton<IAgentWorkspaceStore>(sp =>
                new InterruptAfterJournalStore(new AgentWorkspaceStore(sp.GetRequiredService<IConnectionMultiplexer>(), deployment))));
        });
        await fixture.InitializeAsync();
        try
        {
            var sourceConnection = await fixture.GetTestSecureConnectionIdAsync();
            sourceConnection.Should().NotBeNull("the actual publish validation needs a registered source connection");
            await fixture.Postgres.ExecuteDdlUnderLockAsync($"CREATE TABLE public.{sourceTable} (id SERIAL PRIMARY KEY, name TEXT NOT NULL, geom geometry(Point,4326) NOT NULL); INSERT INTO public.{sourceTable} (name, geom) VALUES ('Original source', ST_SetSRID(ST_Point(1,1),4326));");
            using var bootstrap = fixture.CreateAdminClient();
            var agentKey = await IssueKey(bootstrap, "journal-agent", ["admin:read", "agent:propose-publication", "agent:workspace"]);
            var observerKey = await IssueKey(bootstrap, "journal-observer", ["admin:read", "agent:workspace"]);
            fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = $"apikey:api-key:{agentKey.Id}";
            var bearer = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "journal-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(5));
            bearer.Should().NotBeNull();
            using var human = fixture.CreateClient(c => c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer!.Token));
            using var agent = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", agentKey.Material));
            using var observer = fixture.CreateClient(c => c.DefaultRequestHeaders.Add("X-API-Key", observerKey.Material));
            var created = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "tab-a" }), HttpStatusCode.OK);
            var workspaceId = created.GetProperty("workspaceId").GetString()!;
            var root = $"/api/v1/agent/workspaces/{workspaceId}";
            var draft = await Read(await human.PostAsJsonAsync(root + "/drafts", new
            {
                kind = "publish-layer",
                viewId = "tab-a",
                contextRevision = 0,
                fields = new { connectionId = sourceConnection!.Value.ToString(), schema = "public", table = sourceTable, serviceName = "journal-recovery", layerName = "original-frozen" }
            }), HttpStatusCode.OK);
            var draftId = draft.GetProperty("id").GetString()!;
            var draftPath = root + "/drafts/" + draftId;
            var current = await Read(await human.GetAsync(root + "?viewId=tab-a"), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            {
                turnId = "turn-crash",
                viewId = "tab-a",
                contextRevision = current.GetProperty("revision").GetInt64(),
                prompt = "Publish this exact original draft"
            }), HttpStatusCode.OK);
            var store = fixture.Services.GetRequiredService<IAgentWorkspaceStore>();
            var document = (await store.GetAsync(workspaceId))!;
            (await store.SaveAsync(document with { Members = [.. document.Members, $"apikey:api-key:{observerKey.Id}"] })).Should().BeTrue();
            observer.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            observer.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-crash");
            var observerRequest = new { commandId = "observer-must-not-consume", expectedRevision = 0, viewId = "tab-a" };
            (await observer.PostAsJsonAsync(draftPath + "/submit", observerRequest)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await store.GetAsync(workspaceId))!.Commands.Should().BeEmpty();
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "turn-crash");
            var submit = new { commandId = "command-crash", expectedRevision = 0, viewId = "tab-a" };
            // The decorator throws only AFTER the actual Redis CAS persisted the command.
            (await agent.PostAsJsonAsync(draftPath + "/submit", submit)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var journal = (await store.GetAsync(workspaceId))!.Commands["command-crash"];
            journal.OriginatingTurnId.Should().Be("turn-crash");
            var operations = fixture.Services.GetRequiredService<IOperationInstanceStore>();
            (await operations.GetAsync(journal.OperationInstanceId)).Should().BeNull();
            (await agent.GetAsync(root + "/commands/command-crash")).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await operations.GetAsync(journal.OperationInstanceId)).Should().BeNull();
            await Read(await human.PatchAsJsonAsync(draftPath, new
            {
                viewId = "tab-a",
                expectedRevision = 0,
                changes = new { layerName = "later-human-edit" }
            }), HttpStatusCode.OK);
            var recovered = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            recovered.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(journal.OperationInstanceId);
            var proposalId = recovered.GetProperty("data").GetProperty("proposalId").GetString()!;
            var proposal = await fixture.Services.GetRequiredService<IOperationProposalStore>().GetAsync(proposalId);
            proposal.Should().NotBeNull();
            var payload = JsonSerializer.Deserialize(proposal!.Plan.ExecutionPayload!, ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload)!;
            payload.LayerName.Should().Be("original-frozen");
            var after = (await store.GetAsync(workspaceId))!;
            after.Commands.Should().ContainSingle();
            after.Commands["command-crash"].Should().BeEquivalentTo(journal);
            after.Drafts[draftId].Fields["layerName"].Should().Be("later-human-edit");
            var repeated = await Read(await agent.PostAsJsonAsync(draftPath + "/submit", submit), HttpStatusCode.OK);
            repeated.GetProperty("data").GetProperty("operationInstanceId").GetString().Should().Be(journal.OperationInstanceId);
        }
        finally
        {
            await fixture.Postgres.ExecuteDdlUnderLockAsync($"DROP TABLE IF EXISTS public.{sourceTable}");
            await fixture.DisposeAsync();
        }
    }

    private sealed class InterruptAfterJournalStore(IAgentWorkspaceStore inner) : IAgentWorkspaceStore
    {
        private int _interrupt = 1;
        public bool IsAvailable => inner.IsAvailable;
        public Task<string> GetTargetIdAsync() => inner.GetTargetIdAsync();
        public Task<AgentWorkspaceDocument?> GetAsync(string id) => inner.GetAsync(id);
        public Task<bool> CreateAsync(AgentWorkspaceDocument document) => inner.CreateAsync(document);
        public async Task<bool> SaveAsync(AgentWorkspaceDocument document)
        {
            var saved = await inner.SaveAsync(document);
            if (saved && document.Commands.ContainsKey("command-crash") && Interlocked.Exchange(ref _interrupt, 0) == 1)
                throw new AgentWorkspaceUnavailableException();
            return saved;
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
