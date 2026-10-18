// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceCapturedContextTests
{
    [UnitTheory]
    [InlineData("honua_workspace_create_draft")]
    [InlineData("honua_workspace_set_context")]
    public async Task RealToolCannotSelectLaterViewRevisionFromRetainedTurn(string name)
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
        document = AgentWorkspaceState.AddTurn(document, actor, new("turn-1", "tab-1", 0, "Create a form for this selection"));
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], null, "console"));
        store.GetTargetIdAsync().Returns("target-1");
        store.GetAsync("workspace-1").Returns(_ => document);
        store.SaveAsync(Arg.Any<AgentWorkspaceDocument>()).Returns(call => { document = call.Arg<AgentWorkspaceDocument>(); return true; });
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant).AddSingleton(authorization).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.Headers["X-Honua-Workspace"] = "workspace-1";
        context.Request.Headers["X-Honua-Turn"] = "turn-1";
        var tool = AgentWorkspaceTool.All.Single(tool => tool.Name == name);
        using var arguments = JsonDocument.Parse(name.EndsWith("create_draft", StringComparison.Ordinal)
            ? """{"workspaceId":"workspace-1","viewId":"tab-1","contextRevision":1,"kind":"publish-layer","fields":{"layerName":"later-selection"}}"""
            : """{"workspaceId":"workspace-1","viewId":"tab-1","expectedRevision":1,"targetId":"target-1","selectedResources":[],"task":null}""");
        (await tool.InvokeAsync(context, arguments.RootElement, default)).IsError.Should().BeTrue();
        await store.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
        document.Drafts.Should().BeEmpty();
        document.Turns["turn-1"].Context.Revision.Should().Be(0);
        // Explicit current-user navigation remains available independently of the old turn.
        context.Request.Headers.Clear();
        (await tool.InvokeAsync(context, arguments.RootElement, default)).IsError.Should().NotBeTrue();
        await store.Received(1).SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }
    [UnitTheory]
    [InlineData("project")]
    [InlineData("snapshot")]
    [InlineData("digest")]
    [InlineData("exact")]
    [InlineData("human-selection")]
    public void ImportArtifactMustMatchOriginalCapturedSnapshot(string selection)
    {
        var context = new DefaultHttpContext();
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = "human-1", Members = ["human-1"] };
        var captured = new AgentWorkspaceContext(document.WorkspaceId, document.TargetId, "view-1", 0, [], null);
        document.Turns["turn-1"] = new("turn-1", "human-1", "Import original output", captured, null,
            new("project-original", "snapshot-original", "digest-original", "image-pinned", 1));
        context.Request.Headers["X-Honua-Workspace"] = document.WorkspaceId;
        context.Request.Headers["X-Honua-Turn"] = "turn-1";
        var artifact = new Honua.Core.Features.Import.Domain.DatasetImportArtifactSource(
            selection == "project" ? "later-project" : "project-original",
            selection == "snapshot" ? "later-snapshot" : "snapshot-original",
            selection == "digest" ? "later-digest" : "digest-original", "successful-run", "outputs/points.geojson");
        if (selection == "human-selection") context.Request.Headers.Clear();
        var source = new Honua.Core.Features.Import.Domain.DatasetImportSource("local-process-artifact", "points.geojson", 123, new string('a', 64), artifact);
        var act = () => DatasetImportTaskService.RequireOriginalSource(context, document, source);
        if (selection is "exact" or "human-selection") act.Should().NotThrow();
        else act.Should().Throw<AgentWorkspaceConflictException>();
    }
}
