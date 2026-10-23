// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class StudioDashboardMcpIntegrationTests
{
    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public Task WorkspaceAgent_MapCompositionRequiresExactItemAndDistinctHumanPublication()
        => QualifyAgentPublicationAsync("map");

    [IntegrationTest]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [Endpoint("POST /api/v1/studio/package-drafts")]
    public Task WorkspaceAgent_DashboardCompositionRequiresExactItemAndDistinctHumanPublication()
        => QualifyAgentPublicationAsync("dashboard");

    private async Task QualifyAgentPublicationAsync(string family)
    {
        var itemId = Guid.NewGuid(); // allocated by the trusted controller before issuing the key
        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        (await lifecycle.GetPointersAsync(itemId)).Should().BeNull();
        var keyBody = await ReadHttpAsync(await _client.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name = "exact-studio-agent",
            permissions = new[] { "admin:read", "agent:workspace", $"agent:studio-compose:{itemId:D}", $"agent:studio-propose-publication:{itemId:D}" },
        }), HttpStatusCode.Created);
        var keyId = keyBody.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
        var key = keyBody.GetProperty("data").GetProperty("key").GetString()!;
        _fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = $"apikey:api-key:{keyId}";
        var bearer = _fixture.Services.GetRequiredService<OperatorBearerTokenService>().Issue(
            [new() { Type = ClaimTypes.NameIdentifier, Value = "studio-human-controller" },
             new() { Type = ClaimTypes.Role, Value = "admin" }, new() { Type = "tenant_id", Value = "public" }],
            DateTimeOffset.UtcNow.AddMinutes(10));
        bearer.Should().NotBeNull();
        using var human = _fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer!.Token));
        using var agent = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add("X-API-Key", key));
        var target = await ReadHttpAsync(await human.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
        var workspace = await ReadHttpAsync(await human.PostAsJsonAsync("/api/v1/agent/workspaces", new
        {
            viewId = "studio-native", targetId = target.GetProperty("targetId").GetString(),
        }), HttpStatusCode.OK);
        var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
        await ReadHttpAsync(await human.PostAsJsonAsync($"/api/v1/agent/workspaces/{workspaceId}/turns", new
        {
            turnId = "studio-turn", viewId = "studio-native", contextRevision = 0,
            prompt = "Compose and propose only the controller allocated item.",
        }), HttpStatusCode.OK);
        agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
        agent.DefaultRequestHeaders.Add("X-Honua-Turn", "studio-turn");
        using var initialize = await agent.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0", id = 1, method = "initialize",
            @params = new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "native-studio-agent", version = "1" } },
        });
        initialize.EnsureSuccessStatusCode();
        agent.DefaultRequestHeaders.Add("Mcp-Session-Id", initialize.Headers.GetValues("Mcp-Session-Id").Single());

        foreach (var (view, toolName, expectedFamily) in new[]
                 {
                     ("workspace", "honua_workspace_get_turn", "honua.workspace.canonical"),
                     ("setup", "honua_studio_create_draft", "honua.studio.composition"),
                 })
        {
            var catalog = await ReadHttpAsync(await agent.PostAsJsonAsync("/mcp", new
            {
                jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { view },
            }), HttpStatusCode.OK);
            var descriptor = catalog.GetProperty("result").GetProperty("tools").EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == toolName);
            descriptor.GetProperty("_meta").GetProperty("honua.studio").GetProperty("family")
                .GetString().Should().Be(expectedFamily);
        }

        var denied = await RpcAsync("create_draft", $$$"""{"itemId":"{{{Guid.NewGuid()}}}","packageKey":"foreign-item","family":"{{{family}}}","schemaVersion":"1.0","body":{}}""", agent);
        denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
        denied = await RpcAsync("create_draft", $$$"""{"itemId":"{{{itemId}}}","ownerId":"someone-else","packageKey":"assigned-owner","family":"{{{family}}}","schemaVersion":"1.0","body":{}}""", agent);
        denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();

        var packageKey = $"native-{family}-{itemId:N}";
        var bodyJson = family == "map" ? $$$"""{"mapPackageId":"{{{packageKey}}}","format":"honua_map_package.v1"}""" : "{}";
        var created = await CallAsync("create_draft", $$$"""{"itemId":"{{{itemId}}}","packageKey":"{{{packageKey}}}","family":"{{{family}}}","schemaVersion":"1.0","body":{{{bodyJson}}}}""", agent);
        var draftId = created.GetProperty("draftId").GetGuid();
        (await lifecycle.GetDraftAsync(draftId))!.ItemId.Should().Be(itemId);
        await CallAsync("set_view", $$$"""{"draftId":"{{{draftId}}}","generation":1,"view":{"center":[-157.86,21.31],"zoom":10}}""", agent);
        var draftPath = $"/api/v1/studio/package-drafts/{draftId:D}";
        await ReadHttpAsync(await agent.GetAsync(draftPath), HttpStatusCode.OK); // same item authority over HTTP
        agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
        (await agent.GetAsync(draftPath)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        agent.DefaultRequestHeaders.Add("X-Honua-Turn", "studio-turn");
        await CallAsync("save_version", $$$"""{"draftId":"{{{draftId}}}","generation":2,"changeNote":"Native agent immutable save"}""", agent);
        var pointers = await lifecycle.GetPointersAsync(itemId);
        pointers!.PublishedVersionId.Should().BeNull();
        var versionId = pointers.CurrentVersionId!.Value;
        var version = (await lifecycle.GetVersionAsync(itemId, versionId))!;
        var route = $"/studio/native-{family}-{itemId:N}";
        var proposal = await CallAsync("propose_publication", $$$"""{"itemId":"{{{itemId}}}","versionId":"{{{versionId}}}","contentHash":"{{{version.ContentHash}}}","route":"{{{route}}}","visibility":"personal"}""", agent);
        proposal.GetProperty("humanConfirmationRequired").GetBoolean().Should().BeTrue();
        var proposalId = proposal.GetProperty("proposalId").GetString()!;
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().BeNull();
        (await agent.GetAsync($"/api/v1/admin/proposals/{proposalId}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await agent.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "self approval" })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _client.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "machine admin cannot replace human" })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await ReadHttpAsync(await human.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "Distinct human approves exact native Studio version" }), HttpStatusCode.OK);
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().Be(versionId);
        (await lifecycle.GetVersionAsync(itemId, versionId))!.ContentHash.Should().Be(version.ContentHash);

        // A later proposal cannot borrow the approver's authority after its agent is revoked.
        var currentDraft = (await lifecycle.GetDraftAsync(draftId))!;
        await CallAsync("set_view", $$$"""{"draftId":"{{{draftId}}}","generation":{{{currentDraft.Generation}}},"view":{"center":[-158,22],"zoom":7}}""", agent);
        await CallAsync("save_version", $$$"""{"draftId":"{{{draftId}}}","generation":{{{currentDraft.Generation + 1}}}}""", agent);
        var laterId = (await lifecycle.GetPointersAsync(itemId))!.CurrentVersionId!.Value;
        var later = (await lifecycle.GetVersionAsync(itemId, laterId))!;
        var pending = await ReadHttpAsync(await agent.PostAsJsonAsync(
            $"/api/v1/studio/content-items/{itemId:D}/versions/{laterId:D}/publish-requests",
            new { intent = new { route, visibility = "personal" } }), HttpStatusCode.Accepted);
        var pendingId = pending.GetProperty("data").GetProperty("proposalId").GetString()!;
        later.ContentHash.Should().NotBe(version.ContentHash);
        using var revoke = await _client.PostAsync($"/api/v1/admin/api-keys/{keyId}/revoke", null);
        revoke.EnsureSuccessStatusCode();
        var refused = await ReadHttpAsync(await human.PostAsJsonAsync($"/api/v1/admin/proposals/{pendingId}/approve",
            new { reason = "Revoked agent must fail before publication" }), HttpStatusCode.OK);
        refused.GetProperty("status").GetString().Should().Be("Failed");
        (await lifecycle.GetPointersAsync(itemId))!.PublishedVersionId.Should().Be(versionId);
    }

    private static async Task<JsonElement> ReadHttpAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(expected, body);
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
    }
}
