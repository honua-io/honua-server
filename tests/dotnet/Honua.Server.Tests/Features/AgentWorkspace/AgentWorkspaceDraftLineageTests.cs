// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceDraftLineageTests
{
    [UnitTest]
    public async Task ActualCreateAndPatchToolsAdvanceOnlyTheirOwnOutputAndRejectLaterHumanPatchAndSubmit()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var store = Substitute.For<IAgentWorkspaceStore>();
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = actor, Members = [actor], TenantId = "tenant-1" };
        document.Views["tab-1"] = [AgentWorkspaceState.Current(document, "tab-1")];
        document = AgentWorkspaceState.AddTurn(document, actor, new("turn-1", "tab-1", 0, "Create and edit a new publication form"));
        store.GetTargetIdAsync().Returns("target-1");
        store.GetAsync("workspace-1").Returns(_ => document);
        store.SaveAsync(Arg.Any<AgentWorkspaceDocument>()).Returns(call => { document = call.Arg<AgentWorkspaceDocument>(); return true; });
        var operations = Substitute.For<IOperationInstanceStore>();
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant).AddSingleton(authorization)
            .AddSingleton(operations).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.Headers["X-Honua-Workspace"] = "workspace-1";
        context.Request.Headers["X-Honua-Turn"] = "turn-1";
        using var create = JsonDocument.Parse("""{"workspaceId":"workspace-1","viewId":"tab-1","contextRevision":0,"kind":"publish-layer","fields":{"layerName":"initial"}}""");
        var created = await AgentWorkspaceTool.All.Single(tool => tool.Name == "honua_workspace_create_draft").InvokeAsync(context, create.RootElement, default);
        created.IsError.Should().NotBeTrue();
        var draftId = created.StructuredContent!.Value.GetProperty("id").GetString()!;
        var patchTool = AgentWorkspaceTool.All.Single(tool => tool.Name == "honua_workspace_patch_draft");
        using var patch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            workspaceId = "workspace-1",
            viewId = "tab-1",
            draftId,
            expectedRevision = 0,
            changes = new { layerName = "turn-output" }
        }));
        (await patchTool.InvokeAsync(context, patch.RootElement, default)).IsError.Should().NotBeTrue();
        document.Drafts[draftId].Revision.Should().Be(1);
        document.DraftLineages.Values.Should().ContainSingle().Which.Should().Be(new AgentWorkspaceDraftLineage("turn-1", actor, draftId, 1));
        document.Turns["turn-1"].Context.Task.Should().BeNull();
        // Human editing does not advance the old turn's allowed lineage.
        document = AgentWorkspaceState.PatchDraft(document, draftId, new(1, "tab-1", new() { ["layerName"] = "later-human" }));
        using var laterPatch = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            workspaceId = "workspace-1",
            viewId = "tab-1",
            draftId,
            expectedRevision = 2,
            changes = new { layerName = "must-not-adopt" }
        }));
        (await patchTool.InvokeAsync(context, laterPatch.RootElement, default)).IsError.Should().BeTrue();
        using var submit = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            workspaceId = "workspace-1",
            viewId = "tab-1",
            draftId,
            expectedRevision = 2,
            commandId = "must-not-submit"
        }));
        // No operation invoker is registered: refusal must happen before any dispatch.
        (await AgentWorkspaceTool.All.Single(tool => tool.Name == "honua_workspace_submit_draft").InvokeAsync(context, submit.RootElement, default)).IsError.Should().BeTrue();
        document.Drafts[draftId].Fields["layerName"].Should().Be("later-human");
        document.Commands.Should().BeEmpty();
        document.DraftLineages.Values.Should().ContainSingle().Which.Revision.Should().Be(1);
        var original = new AgentWorkspaceCommand("known", actor, draftId, 1, "original-key", "operation-1",
            new() { ["layerName"] = "turn-output" }, DateTimeOffset.UtcNow.AddDays(-1), "turn-1");
        document.Commands[original.CommandId] = original;
        context.Request.RouteValues["workspaceId"] = "workspace-1";
        context.Request.RouteValues["draftId"] = draftId;
        store.ClearReceivedCalls();
        var accepted = new OperationHandle
        {
            OperationInstanceId = "operation-1",
            OperationId = "service.publish",
            Status = OperationHandleStatus.RequiresApproval,
            CorrelationId = "correlation-1",
            TenantId = "tenant-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        operations.GetAsync("operation-1", Arg.Any<CancellationToken>()).Returns(accepted);
        // An accepted operation remains observable after later typing and its original
        // retry deadline, without requiring any operation invoker in this composition.
        var known = await AgentWorkspaceEndpoints.SubmitDraft(context, store, new("known", 1, "tab-1"));
        ((IStatusCodeHttpResult)known).StatusCode.Should().Be(200);
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
        var alias = await AgentWorkspaceEndpoints.SubmitDraft(context, store, new("alias", 1, "tab-1"));
        ((IStatusCodeHttpResult)alias).StatusCode.Should().Be(200);
        document.Commands["alias"].OriginatingTurnId.Should().Be("turn-1");
        document.Commands["alias"].RetryBefore.Should().Be(original.RetryBefore);
        document.Commands["alias"].Fields.Should().Equal(original.Fields);
        await operations.Received(4).GetAsync("operation-1", Arg.Any<CancellationToken>());
        await operations.DidNotReceive().TryCreateAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>());
        await operations.DidNotReceive().SetAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void CapturedTaskCannotBorrowAnotherExecutorsRevisionOrAnotherView()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        var actor = CanonicalSecurityActor.Resolve(context.User)!.ActorId;
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = actor, Members = [actor], TenantId = "tenant-1" };
        var draft = new AgentWorkspaceDraft("draft-1", 0, "publish-layer", new() { ["layerName"] = "original" }, []);
        document.Drafts[draft.Id] = draft;
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], new(draft.Id, 0), "console"));
        document = AgentWorkspaceState.AddTurn(document, actor, new("turn-1", "tab-1", 1, "Edit this original form"));
        context.Request.Headers["X-Honua-Workspace"] = "workspace-1";
        context.Request.Headers["X-Honua-Turn"] = "turn-1";
        var lineage = AgentWorkspaceDraftLineages.RequireEditable(context, document, draft, "tab-1", 0);
        lineage!.ActorId.Should().Be(actor);
        document = AgentWorkspaceDraftLineages.Record(document, lineage with { ActorId = "different-executor" });
        var borrowed = () => AgentWorkspaceDraftLineages.RequireEditable(context, document, draft, "tab-1", 0);
        borrowed.Should().Throw<AgentWorkspaceConflictException>();
        var otherView = () => AgentWorkspaceDraftLineages.RequireEditable(context, document, draft, "tab-2", 0);
        otherView.Should().Throw<AgentWorkspaceConflictException>();
    }
}
