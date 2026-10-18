// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Styling;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.OgcApiStyles, TestProtocols.Mcp)]
public sealed class SharedStyleAgentJourneyHttpTests(RedisFixture redis)
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("GET /ogc/styles/{styleId}/impact")]
    [Endpoint("POST /mcp tools/call honua_shared_style_get")]
    [Endpoint("POST /mcp tools/call honua_shared_style_propose")]
    [Endpoint("POST /api/v1/admin/proposals/{id}/approve")]
    [Endpoint("PUT /api/v1/admin/metadata/layers/{layerId}/style")]
    [Endpoint("POST /api/v1/admin/metadata/layers/{layerId}/style/import-sld")]
    public async Task FrozenSharedStyle_AgentProposes_HumanApproves_ExactBytesCommitAndLostResponseReconciles()
    {
        var fixture = new WebAppFixture().UseKestrel().ConfigureWebHost(builder =>
        {
            builder.UseSetting("ConnectionStrings:redis", redis.ConnectionString);
            builder.UseSetting("Licensing:DevGrantEdition", "Pro");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            builder.UseSetting("AgentWorkspace:DeploymentId", "shared-style-http-" + Guid.NewGuid().ToString("N"));
            builder.UseSetting("AgentWorkspace:ReleaseId", "shared-style-preview");
            builder.UseSetting("Authentication:OperatorBearer:Enabled", "true");
            builder.UseSetting("Authentication:OperatorBearer:SigningKey", "shared-style-http-only-signing-key-at-least-32-bytes");
            builder.UseSetting("OperatorApproval:PublishRequiresApproval", "false");
            builder.UseSetting("OperatorApproval:AdminExemptFromApproval", "true");
        });
        await fixture.InitializeAsync();
        try
        {
            using var bootstrap = fixture.CreateAdminClient();
            var issued = await Read(await bootstrap.PostAsJsonAsync("/api/v1/admin/api-keys", new
            { name = "shared-style-agent", permissions = new[] { "admin", "read:*", "agent:workspace" } }), HttpStatusCode.Created);
            var material = issued.GetProperty("data").GetProperty("key").GetString()!;
            var keyId = issued.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
            fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = "apikey:api-key:" + keyId;
            var token = fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
                [new() { Type = ClaimTypes.NameIdentifier, Value = "shared-style-human" }, new() { Type = ClaimTypes.Role, Value = "admin" },
                 new() { Type = "tenant_id", Value = "public" }], DateTimeOffset.UtcNow.AddMinutes(10));
            token.Should().NotBeNull();
            using var human = fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.Token));
            using var agent = fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", material));
            using (var legacy = await agent.PutAsJsonAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style",
                new { mapLibreStyle = new { version = 8, sources = new { }, layers = Array.Empty<object>() } }))
                legacy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using (var legacySld = await agent.PostAsync($"/api/v1/admin/metadata/layers/{WebAppFixture.TestLayerId}/style/import-sld",
                new StringContent("<StyledLayerDescriptor/>", Encoding.UTF8, "application/xml")))
                legacySld.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var styleId = "shared-style-journey-" + Guid.NewGuid().ToString("N");
            const string initial = """{"version":8,"sources":{},"layers":[{"id":"background","type":"background","paint":{"background-color":"#112233"}}]}""";
            const string proposed = """{"version":8,"sources":{},"layers":[{"id":"background","type":"background","paint":{"background-color":"#8844aa"}}]}""";
            using (var create = new HttpRequestMessage(HttpMethod.Post, "/ogc/styles")
            { Content = new StringContent(initial, Encoding.UTF8, "application/vnd.mapbox.style+json") })
            {
                create.Headers.Add("X-Style-Id", styleId);
                using var response = await human.SendAsync(create);
                response.StatusCode.Should().Be(HttpStatusCode.Created);
            }
            var impact = await Read(await human.GetAsync("/ogc/styles/" + styleId + "/impact"), HttpStatusCode.OK);
            var target = (await Read(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK)).GetProperty("targetId").GetString()!;
            var workspace = await Read(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new { viewId = "shared-style-tab", targetId = target }), HttpStatusCode.OK);
            var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
            var root = "/api/v1/agent/workspaces/" + workspaceId;
            var task = new { kind = "shared-style", id = styleId, revision = impact.GetProperty("styleVersion").GetInt32(),
                contentHash = impact.GetProperty("contentHash").GetString(), impactHash = impact.GetProperty("impactHash").GetString() };
            await Read(await human.PutAsJsonAsync(root + "/context", new
            { viewId = "shared-style-tab", expectedRevision = 0, targetId = target, selectedResources = Array.Empty<object>(), task }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            { turnId = "style-turn", viewId = "shared-style-tab", contextRevision = 1, prompt = "Make this shared background purple and propose its exact impact for review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "style-turn");
            var actual = await Tool(agent, "honua_shared_style_get", new { workspaceId, turnId = "style-turn" });
            actual.GetProperty("impact").GetProperty("contentHash").GetString().Should().Be(task.contentHash);
            actual.GetProperty("style").GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");
            using var candidate = JsonDocument.Parse(proposed);
            var arguments = new { workspaceId, turnId = "style-turn", intent = new
            { styleId, styleVersion = task.revision, task.contentHash, task.impactHash, style = candidate.RootElement.Clone() } };
            var handle = await Tool(agent, "honua_shared_style_propose", arguments);
            handle.GetProperty("status").GetString().Should().Be("RequiresApproval");
            var proposalId = handle.GetProperty("proposalId").GetString()!;
            var operationId = handle.GetProperty("operationInstanceId").GetString()!;
            (await Tool(agent, "honua_shared_style_propose", arguments)).GetProperty("operationInstanceId").GetString().Should().Be(operationId);
            await using var scope = fixture.Services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<IStyleCatalog>();
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision);
            var proposalStore = scope.ServiceProvider.GetRequiredService<IOperationProposalStore>();
            var instanceStore = scope.ServiceProvider.GetRequiredService<IOperationInstanceStore>();
            var waitingProposal = (await proposalStore.GetAsync(proposalId))!;
            var waitingHandle = (await instanceStore.GetAsync(operationId))!;
            // A durable human claim without SQL proof must remain exactly unchanged on actual MCP status read.
            await SetProposalState(proposalStore, waitingProposal with { Status = OperationProposalStatus.Executing,
                ApprovedByAtClaim = "shared-style-human", ApprovedAtClaim = DateTimeOffset.UtcNow });
            var beforeMissingReceipt = (await proposalStore.GetAsync(proposalId))!;
            (await ReadProposal(agent, proposalId)).GetProperty("status").GetString().Should().Be("Executing");
            (await proposalStore.GetAsync(proposalId)).Should().BeEquivalentTo(beforeMissingReceipt);
            (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(waitingHandle);
            await SetProposalState(proposalStore, waitingProposal);
            (await agent.PostAsync("/api/v1/admin/proposals/" + proposalId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using var inbox = await human.GetAsync("/api/v1/admin/proposals/" + proposalId);
            var inboxText = await inbox.Content.ReadAsStringAsync();
            inbox.StatusCode.Should().Be(HttpStatusCode.OK);
            inboxText.Contains(material, StringComparison.Ordinal).Should().BeFalse();
            inboxText.Contains("executionPayload", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            (await human.PostAsync("/api/v1/admin/proposals/" + proposalId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await scope.ServiceProvider.GetRequiredService<IOperationProposalStore>().GetAsync(proposalId))!.Status.Should().Be(OperationProposalStatus.Succeeded);
            var committed = (await catalog.GetStyleAsync(styleId))!;
            committed.StyleVersion.Should().Be(task.revision + 1);
            using var stored = JsonDocument.Parse(committed.MapLibreStyleJson);
            stored.RootElement.GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#8844aa");
            // Reconstruct the durable records left by a process stop after the actual SQL commit,
            // before canonical completion persistence. This is a crash-window regression, not a process-kill certification.
            var successfulProposal = (await proposalStore.GetAsync(proposalId))!;
            successfulProposal.ApprovedByAtClaim.Should().NotBeNullOrWhiteSpace();
            successfulProposal.ApprovedAtClaim.Should().NotBeNull();
            await SetProposalState(proposalStore, successfulProposal with { Status = OperationProposalStatus.Executing,
                ResolvedBy = null, ResolvedAt = null });
            await instanceStore.SetAsync(waitingHandle with { Status = OperationHandleStatus.Accepted });
            var recovered = await Task.WhenAll(ReadProposal(agent, proposalId), ReadProposal(agent, proposalId));
            recovered.Should().OnlyContain(value => value.GetProperty("status").GetString() == "Succeeded");
            var recoveredProposal = (await proposalStore.GetAsync(proposalId))!;
            recoveredProposal.ResolvedBy.Should().Be(successfulProposal.ApprovedByAtClaim);
            recoveredProposal.Audit.OperationInstanceId.Should().Be(operationId);
            (await instanceStore.GetAsync(operationId))!.Status.Should().Be(OperationHandleStatus.Completed);
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            // The reverse ordering (original completion already persisted) is a terminal no-op for the handle.
            var terminal = (await instanceStore.GetAsync(operationId))!;
            await SetProposalState(proposalStore, recoveredProposal with { Status = OperationProposalStatus.Executing });
            (await ReadProposal(agent, proposalId)).GetProperty("status").GetString().Should().Be("Succeeded");
            (await instanceStore.GetAsync(operationId)).Should().BeEquivalentTo(terminal);
            // A lost response reconciles the canonical operation even though its own mutation advanced the focused style.
            (await Tool(agent, "honua_shared_style_propose", arguments)).GetProperty("operationInstanceId").GetString().Should().Be(operationId);
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            var staleRead = await Rpc(agent, "honua_shared_style_get", new { workspaceId, turnId = "style-turn" });
            IsFailure(staleRead).Should().BeTrue("a frozen read cannot quietly retarget to different current bytes");
            // Restoration is a new reviewed operation using retained canonical before bytes, never an old version label.
            var restoreImpact = await Read(await human.GetAsync("/ogc/styles/" + styleId + "/impact"), HttpStatusCode.OK);
            await Read(await human.PutAsJsonAsync(root + "/context", new
            { viewId = "shared-style-tab", expectedRevision = 1, targetId = target, selectedResources = Array.Empty<object>(),
                task = new { kind = "shared-style", id = styleId, revision = restoreImpact.GetProperty("styleVersion").GetInt32(),
                    contentHash = restoreImpact.GetProperty("contentHash").GetString(), impactHash = restoreImpact.GetProperty("impactHash").GetString() } }), HttpStatusCode.OK);
            await Read(await human.PostAsJsonAsync(root + "/turns", new
            { turnId = "restore-turn", viewId = "shared-style-tab", contextRevision = 2, prompt = "Restore the retained prior shared appearance for human review." }), HttpStatusCode.OK);
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "restore-turn");
            var restore = await Tool(agent, "honua_shared_style_propose", new
            { workspaceId, turnId = "restore-turn", restoreFromProposalId = proposalId });
            restore.GetProperty("status").GetString().Should().Be("RequiresApproval");
            (await catalog.GetStyleAsync(styleId))!.StyleVersion.Should().Be(task.revision + 1);
            var restoreId = restore.GetProperty("proposalId").GetString()!;
            restoreId.Should().NotBe(proposalId);
            (await human.PostAsync("/api/v1/admin/proposals/" + restoreId + "/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            var restored = (await catalog.GetStyleAsync(styleId))!;
            restored.StyleVersion.Should().Be(task.revision + 2);
            using var restoredBytes = JsonDocument.Parse(restored.MapLibreStyleJson);
            restoredBytes.RootElement.GetProperty("layers")[0].GetProperty("paint").GetProperty("background-color").GetString().Should().Be("#112233");

        }
        finally { await fixture.DisposeAsync(); }
    }

    private static async Task<JsonElement> Tool(HttpClient client, string name, object arguments)
    {
        var envelope = await Rpc(client, name, arguments);
        IsFailure(envelope).Should().BeFalse();
        return envelope.GetProperty("result").GetProperty("structuredContent").Clone();
    }
    private static bool IsFailure(JsonElement envelope) => envelope.TryGetProperty("error", out _) ||
        envelope.TryGetProperty("result", out var result) && result.TryGetProperty("isError", out var error) && error.GetBoolean();
    private static async Task SetProposalState(IOperationProposalStore store, OperationProposal proposal)
    {
        var current = (await store.GetAsync(proposal.ProposalId))!;
        (await store.TrySetAsync(proposal with { Version = current.Version })).Should().BeTrue();
    }

    private static async Task<JsonElement> ReadProposal(HttpClient client, string id)
    {
        var envelope = await Read(await client.PostAsJsonAsync("/mcp", new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "resources/read", @params = new { uri = "honua://proposals/" + id } }), HttpStatusCode.OK);
        envelope.TryGetProperty("error", out _).Should().BeFalse();
        using var parsed = JsonDocument.Parse(envelope.GetProperty("result").GetProperty("contents")[0].GetProperty("text").GetString()!);
        return parsed.RootElement.Clone();
    }

    private static async Task<JsonElement> Rpc(HttpClient client, string name, object arguments)
        => await Read(await client.PostAsJsonAsync("/mcp", new
        { jsonrpc = "2.0", id = Guid.NewGuid().ToString("N"), method = "tools/call", @params = new { name, arguments } }), HttpStatusCode.OK);
    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            response.StatusCode.Should().Be(expected);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return document.RootElement.Clone();
        }
    }
}
