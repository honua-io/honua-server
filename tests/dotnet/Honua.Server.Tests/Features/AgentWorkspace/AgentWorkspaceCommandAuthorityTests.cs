// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceCommandAuthorityTests
{
    [UnitTest]
    public async Task CurrentWorkspaceMembershipCannotReadAnotherActorsKnownCommand()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = actor, Members = [actor, "another-actor"], TenantId = "tenant-1" };
        document.Commands["known"] = new("known", "another-actor", "draft-1", 0, "key", "operation-1", new(), DateTimeOffset.UtcNow.AddHours(1));
        var store = Substitute.For<IAgentWorkspaceStore>();
        store.GetTargetIdAsync().Returns("target-1");
        store.GetAsync("workspace-1").Returns(document);
        using var services = new ServiceCollection().AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.RouteValues["workspaceId"] = "workspace-1";
        context.Request.RouteValues["commandId"] = "known";
        // No operation store is registered: foreign actor refusal precedes reading any handle.
        var result = await AgentWorkspaceEndpoints.GetCommand(context, store);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(404);
    }

    [UnitTest]
    public async Task RetainedCredentialRevokedDuringStatusReadCannotProjectReceipt()
    {
        var keys = new InMemoryAdminApiKeyStore();
        var created = await keys.CreateAsync("status-agent", ["admin:read", "agent:workspace"], null, null, default);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        using var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).AddSingleton(authorization).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.Path = "/api/v1/agent/workspaces/workspace-1/commands/known";
        var operations = Substitute.For<IOperationInstanceStore>();
        var delayed = new TaskCompletionSource<OperationHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.GetAsync("operation-1", Arg.Any<CancellationToken>()).Returns(delayed.Task);
        var pending = OperationsEndpoints.HandleGetBoundHandleStatus(context, "operation-1", operations, "tenant-1", true, default);
        pending.IsCompleted.Should().BeFalse();
        await keys.RevokeAsync(created.Record.Id, default);
        delayed.SetResult(Handle("operation-1", "tenant-1"));
        (await pending).Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        await operations.DidNotReceive().SetAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("operation-1", "tenant-2")]
    [InlineData("different-operation", "tenant-1")]
    public async Task WorkspaceReceiptRequiresExactTenantAndOperationIdentity(string actualId, string actualTenant)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "platform-human"), new Claim(ClaimTypes.Role, "platform_admin")], "Bearer"));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        using var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var operations = Substitute.For<IOperationInstanceStore>();
        operations.GetAsync("operation-1", Arg.Any<CancellationToken>()).Returns(Handle(actualId, actualTenant));
        var result = await OperationsEndpoints.HandleGetBoundHandleStatus(context, "operation-1", operations, "tenant-1", true, default);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(404);
    }

    [UnitTheory]
    [InlineData("membership")]
    [InlineData("journal")]
    [InlineData("deployment-target")]
    public async Task MembershipJournalOrDeploymentChangeDuringHandleReadCannotProjectEarlierAuthorizedReceipt(string change)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
        var document = new AgentWorkspaceDocument
        { WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = actor, Members = [actor], TenantId = "tenant-1" };
        var original = new AgentWorkspaceCommand("known", actor, "draft-1", 0, "key", "operation-1", new() { ["table"] = "original" }, DateTimeOffset.UtcNow.AddHours(1));
        document.Commands[original.CommandId] = original;
        var workspace = Substitute.For<IAgentWorkspaceStore>();
        workspace.GetTargetIdAsync().Returns("target-1");
        workspace.GetAsync("workspace-1").Returns(_ => document);
        var operations = Substitute.For<IOperationInstanceStore>();
        var delayed = new TaskCompletionSource<OperationHandle?>(TaskCreationOptions.RunContinuationsAsynchronously);
        operations.GetAsync("operation-1", Arg.Any<CancellationToken>()).Returns(delayed.Task);
        using var services = new ServiceCollection().AddSingleton(tenant).AddSingleton(authorization).AddSingleton(operations).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.RouteValues["workspaceId"] = "workspace-1";
        context.Request.RouteValues["commandId"] = "known";
        var pending = AgentWorkspaceEndpoints.GetCommand(context, workspace);
        pending.IsCompleted.Should().BeFalse();
        if (change == "journal")
            document = document with
            {
                Commands = new(document.Commands, StringComparer.Ordinal)
                { ["known"] = original with { Fields = new() { ["table"] = "changed-during-read" } } }
            };
        else if (change == "deployment-target") workspace.GetTargetIdAsync().Returns("target-2");
        else document = document with { Members = [] };
        delayed.SetResult(Handle("operation-1", "tenant-1"));
        ((IStatusCodeHttpResult)await pending).StatusCode.Should().Be(404);
        await workspace.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
        await operations.DidNotReceive().SetAsync(Arg.Any<OperationHandle>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("membership")]
    [InlineData("turn")]
    [InlineData("deployment-target")]
    [InlineData("unrelated-navigation")]
    public async Task TurnResponseRechecksRetainedAuthorityAfterAssemblingMessages(string change)
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human-1")], "Bearer");
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        var captured = new AgentWorkspaceContext("workspace-1", "target-1", "view-1", 0, [], null, "tenant-1");
        var turn = new AgentWorkspaceTurn("turn-1", actor, "original", captured, null);
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = actor,
            Members = [actor],
            TenantId = "tenant-1",
            Turns = new() { [turn.TurnId] = turn },
            Messages = [new("message-1", turn.TurnId, "user", "private original prompt")],
        };
        var workspace = Substitute.For<IAgentWorkspaceStore>();
        workspace.GetTargetIdAsync().Returns("target-1");
        var finalRead = new TaskCompletionSource<AgentWorkspaceDocument?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        workspace.GetAsync("workspace-1").Returns(_ => ++reads == 1 ? Task.FromResult<AgentWorkspaceDocument?>(document) : finalRead.Task);
        using var services = new ServiceCollection().AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        context.Request.RouteValues["workspaceId"] = "workspace-1";
        context.Request.RouteValues["turnId"] = turn.TurnId;
        var pending = AgentWorkspaceEndpoints.GetTurn(context, workspace);
        pending.IsCompleted.Should().BeFalse();
        if (change == "membership") document = document with { Members = [] };
        else if (change == "turn") document = document with { Version = document.Version + 1, Turns = [] };
        else if (change == "deployment-target") workspace.GetTargetIdAsync().Returns("target-2");
        else document = document with
        {
            Version = document.Version + 1,
            Views = new() { ["other-view"] = [captured with { ViewId = "other-view", Revision = 7 }] }
        };
        finalRead.SetResult(document);
        ((IStatusCodeHttpResult)await pending).StatusCode.Should().Be(change == "unrelated-navigation" ? 200 : 404);
        await workspace.DidNotReceive().SaveAsync(Arg.Any<AgentWorkspaceDocument>());
    }

    private static OperationHandle Handle(string id, string tenant) => new()
    {
        OperationInstanceId = id,
        OperationId = "service.publish",
        TenantId = tenant,
        Status = OperationHandleStatus.RequiresApproval,
        CorrelationId = "correlation-1",
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };
}
