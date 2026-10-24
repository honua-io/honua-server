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
    [IntegrationTheory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "missing-grant")]
    [InlineData(true, "missing-grant")]
    [InlineData(false, "wrong-hash")]
    [InlineData(true, "wrong-hash")]
    [InlineData(false, "wrong-turn")]
    [InlineData(true, "wrong-turn")]
    [InlineData(false, "stale")]
    [InlineData(true, "stale")]
    [InlineData(false, "unsaved-edit")]
    [InlineData(true, "unsaved-edit")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    [Operation(Operations.StudioLifecycle)]
    [Endpoint("POST /mcp")]
    [Endpoint("GET /api/v1/studio/content-items/{itemId}/versions")]
    [Endpoint("POST /api/v1/studio/content-items/{itemId}/rollback-requests")]
    public async Task WorkspaceAgent_RestoreSavedVersion_RequiresSeparateApprovalAndPreservesDraft(bool useMcp, string failure)
    {
        var lifecycle = _fixture.Services.GetRequiredService<IStudioPackageLifecycleService>();
        var itemId = Guid.NewGuid();
        var packageKey = $"restore-map-{itemId:N}";
        var created = await CallAsync("create_draft", $$$"""
            {"itemId":"{{{itemId}}}","packageKey":"{{{packageKey}}}","family":"map","schemaVersion":"1.0",
             "body":{"mapPackageId":"{{{packageKey}}}","format":"honua_map_package.v1"}}
            """);
        var draftId = created.GetProperty("draftId").GetGuid();
        var saved = await CallAsync("save_version", $$$"""{"draftId":"{{{draftId}}}","generation":1}""");
        var original = saved.GetProperty("version");
        var originalId = original.GetProperty("versionId").GetGuid();
        var originalHash = original.GetProperty("contentHash").GetString()!;
        var draft = (await lifecycle.GetDraftAsync(draftId))!;
        await CallAsync("set_view", $$$"""
            {"draftId":"{{{draftId}}}","generation":{{{draft.Generation}}},"view":{"center":[-157.86,21.31],"zoom":12}}
            """);
        draft = (await lifecycle.GetDraftAsync(draftId))!;
        saved = await CallAsync("save_version", $$$"""{"draftId":"{{{draftId}}}","generation":{{{draft.Generation}}}}""");
        var laterId = saved.GetProperty("version").GetProperty("versionId").GetGuid();
        laterId.Should().NotBe(originalId);
        saved.GetProperty("version").GetProperty("contentHash").GetString().Should().NotBe(originalHash);
        var preservedDraft = (await lifecycle.GetDraftAsync(draftId))!;

        var permissions = new List<string> { "admin:read", "agent:workspace", $"agent:studio-compose:{itemId:D}" };
        if (failure != "missing-grant") permissions.Add($"agent:studio-restore:{itemId:D}");
        var keyBody = await ReadHttpAsync(await _client.PostAsJsonAsync("/api/v1/admin/api-keys", new
        {
            name = "exact-studio-restoration",
            permissions = permissions.ToArray(),
        }), HttpStatusCode.Created);
        var keyId = keyBody.GetProperty("data").GetProperty("apiKey").GetProperty("id").GetString()!;
        _fixture.Services.GetRequiredService<IConfiguration>()["AgentWorkspace:LocalAgentPrincipalId"] = $"apikey:api-key:{keyId}";
        using var agent = _fixture.CreateClient(client => client.DefaultRequestHeaders.Add(
            "X-API-Key", keyBody.GetProperty("data").GetProperty("key").GetString()!));
        var tokens = _fixture.Services.GetRequiredService<OperatorBearerTokenService>();
        var authorToken = tokens.Issue(
            [new() { Type = ClaimTypes.NameIdentifier, Value = "restore-author" },
             new() { Type = ClaimTypes.Role, Value = "admin" }, new() { Type = "tenant_id", Value = "public" }],
            DateTimeOffset.UtcNow.AddMinutes(10))!;
        var approverToken = tokens.Issue(
            [new() { Type = ClaimTypes.NameIdentifier, Value = "restore-approver" },
             new() { Type = ClaimTypes.Role, Value = "admin" }, new() { Type = "tenant_id", Value = "public" }],
            DateTimeOffset.UtcNow.AddMinutes(10))!;
        using var author = _fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authorToken.Token));
        using var approver = _fixture.CreateClient(client => client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", approverToken.Token));
        var target = await ReadHttpAsync(await author.GetAsync("/api/v1/agent/target"), HttpStatusCode.OK);
        var workspace = await ReadHttpAsync(await author.PostAsJsonAsync("/api/v1/agent/workspaces", new
        {
            viewId = "studio-restore",
            targetId = target.GetProperty("targetId").GetString(),
        }), HttpStatusCode.OK);
        var workspaceId = workspace.GetProperty("workspaceId").GetString()!;
        await ReadHttpAsync(await author.PostAsJsonAsync($"/api/v1/agent/workspaces/{workspaceId}/turns", new
        {
            turnId = "restore-turn",
            viewId = "studio-restore",
            contextRevision = 0,
            prompt = "Restore the previous saved map version and preserve my editable draft.",
        }), HttpStatusCode.OK);
        agent.DefaultRequestHeaders.Add("X-Honua-Workspace", workspaceId);
        agent.DefaultRequestHeaders.Add("X-Honua-Turn", "restore-turn");
        var idempotencyKey = $"restore-original-{itemId:N}";
        agent.DefaultRequestHeaders.Add("Idempotency-Key", idempotencyKey);

        var history = await ReadHttpAsync(await agent.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions"), HttpStatusCode.OK);
        var before = history.GetProperty("data").GetProperty("pointers");
        before.GetProperty("currentVersionId").GetGuid().Should().Be(laterId);
        var revision = before.GetProperty("pointerRevision").GetInt64();
        var originalRead = await ReadHttpAsync(await agent.GetAsync($"/api/v1/studio/content-items/{itemId:D}/versions/{originalId:D}"), HttpStatusCode.OK);
        originalRead.GetProperty("data").GetProperty("contentHash").GetString().Should().Be(originalHash);
        if (useMcp)
        {
            var observed = await CallAsync("get_version", $$$"""{"itemId":"{{{itemId}}}","versionId":"{{{originalId}}}"}""", agent);
            observed.GetProperty("contentHash").GetString().Should().Be(originalHash);
            observed.GetProperty("pointers").GetProperty("pointerRevision").GetInt64().Should().Be(revision);
        }
        var request = new
        {
            itemId,
            targetVersionId = originalId,
            pointer = "current",
            reason = "Restore original saved version",
            contentHash = failure == "wrong-hash" ? "not-the-original-content-hash" : originalHash,
            expectedCurrentVersionId = laterId,
            expectedPointerRevision = revision,
            idempotencyKey,
        };
        var serialized = JsonSerializer.Serialize(request);
        if (failure == "wrong-turn")
        {
            agent.DefaultRequestHeaders.Remove("X-Honua-Turn");
            agent.DefaultRequestHeaders.Add("X-Honua-Turn", "not-the-author-turn");
        }
        if (failure is "missing-grant" or "wrong-turn" or "wrong-hash")
        {
            if (useMcp && failure == "wrong-turn")
            {
                using var denied = await agent.PostAsJsonAsync("/mcp", new
                {
                    jsonrpc = "2.0",
                    id = 1,
                    method = "tools/call",
                    @params = new { name = "honua_studio_propose_restore", arguments = request },
                });
                denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            else if (useMcp)
            {
                var denied = await RpcAsync("propose_restore", serialized, agent);
                denied.GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();
            }
            else
            {
                await ReadHttpAsync(await agent.PostAsJsonAsync($"/api/v1/studio/content-items/{itemId:D}/rollback-requests", request),
                    failure == "wrong-hash" ? HttpStatusCode.Conflict : HttpStatusCode.Forbidden);
            }
            (await lifecycle.GetPointersAsync(itemId))!.CurrentVersionId.Should().Be(laterId);
            (await lifecycle.ListVersionsAsync(itemId)).Should().HaveCount(2);
            return;
        }

        var pending = useMcp
            ? (await CallAsync("propose_restore", serialized, agent)).GetProperty("operation")
            : (await ReadHttpAsync(await agent.PostAsJsonAsync($"/api/v1/studio/content-items/{itemId:D}/rollback-requests", request),
                HttpStatusCode.Accepted)).GetProperty("data");
        var proposalId = pending.GetProperty("proposalId").GetString()!;
        (await lifecycle.GetPointersAsync(itemId))!.CurrentVersionId.Should().Be(laterId);
        var proposal = await ReadHttpAsync(await agent.GetAsync($"/api/v1/admin/proposals/{proposalId}"), HttpStatusCode.OK);
        var visibleDiff = string.Join("\n", proposal.GetProperty("diff").EnumerateArray().Select(value => value.GetString()));
        visibleDiff.Should().Contain(originalId.ToString("D")).And.Contain(originalHash).And.Contain(laterId.ToString("D"));
        (await agent.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve", new { reason = "self approval" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var expectedPointer = laterId;
        if (failure is "stale" or "unsaved-edit")
        {
            var humanDraft = (await lifecycle.GetDraftAsync(draftId))!;
            await CallAsync("set_view", $$$"""{"draftId":"{{{draftId}}}","generation":{{{humanDraft.Generation}}},"view":{"center":[-158,22],"zoom":7}}""");
            humanDraft = (await lifecycle.GetDraftAsync(draftId))!;
            if (failure == "stale")
            {
                var humanSave = await CallAsync("save_version", $$$"""{"draftId":"{{{draftId}}}","generation":{{{humanDraft.Generation}}}}""");
                expectedPointer = humanSave.GetProperty("version").GetProperty("versionId").GetGuid();
            }
            preservedDraft = (await lifecycle.GetDraftAsync(draftId))!;
        }
        if (failure == "revoked")
        {
            using var revoke = await _client.PostAsync($"/api/v1/admin/api-keys/{keyId}/revoke", null);
            revoke.EnsureSuccessStatusCode();
        }
        var approval = await ReadHttpAsync(await approver.PostAsJsonAsync($"/api/v1/admin/proposals/{proposalId}/approve",
            new { reason = "Approve the exact saved map restoration" }), HttpStatusCode.OK);
        var after = (await lifecycle.GetPointersAsync(itemId))!;
        after.PublishedVersionId.Should().BeNull();
        var draftAfter = (await lifecycle.GetDraftAsync(draftId))!;
        draftAfter.Generation.Should().Be(preservedDraft.Generation);
        draftAfter.Envelope.Body.Should().NotBeNull();
        draftAfter.Envelope.Body!.Value.GetRawText().Should().Be(preservedDraft.Envelope.Body!.Value.GetRawText());
        if (failure is "stale" or "revoked")
        {
            approval.GetProperty("status").GetString().Should().Be("Failed");
            after.CurrentVersionId.Should().Be(expectedPointer);
            return;
        }

        after.CurrentVersionId.Should().Be(originalId);
        after.PointerRevision.Should().Be(revision + 1);
        (await lifecycle.GetVersionAsync(itemId, originalId))!.ContentHash.Should().Be(originalHash);
        var retried = useMcp
            ? (await CallAsync("propose_restore", serialized, agent)).GetProperty("restoration")
            : (await ReadHttpAsync(await agent.PostAsJsonAsync($"/api/v1/studio/content-items/{itemId:D}/rollback-requests", request),
                HttpStatusCode.Created)).GetProperty("data");
        retried.GetProperty("targetVersionId").GetGuid().Should().Be(originalId);
        (await lifecycle.GetPointersAsync(itemId)).Should().Be(after);
        (await lifecycle.ListVersionsAsync(itemId)).Should().HaveCount(2);
    }
}
