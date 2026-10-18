// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceWorkflowTaskTests
{
    private static readonly string Hash = new('a', 64);
    private static readonly AgentTaskReference Reference = new("pkg-分析-1", 2, "workflow-package", ContentHash: Hash);

    [UnitTest]
    public async Task WorkflowContextRequiresStoredOwnershipTenantVersionAndHash()
    {
        var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Bearer"));
        var owner = CanonicalSecurityActor.Resolve(human)!.ActorId;
        var store = Substitute.For<IWorkflowPackageStore>();
        var version = Version(owner);
        store.GetVersionAsync(Reference.Id, 2, Arg.Any<CancellationToken>()).Returns(version);
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = human, RequestServices = services };

        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference)).Should().BeTrue();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference with { ContentHash = new('b', 64) })).Should().BeFalse();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference with { Revision = 3 })).Should().BeFalse();
        tenant.TenantId.Returns("other-tenant");
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference)).Should().BeFalse();
        tenant.TenantId.Returns("tenant-1");
        store.GetVersionAsync(Reference.Id, 2, Arg.Any<CancellationToken>()).Returns(version with { Ownership = null });
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference)).Should().BeFalse();
        store.GetVersionAsync(Reference.Id, 2, Arg.Any<CancellationToken>()).Returns(Version("another-owner"));
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference)).Should().BeFalse();
    }

    [UnitTest]
    public async Task AgentDelegationRequiresExactScopeCurrentMembershipAndWorkspaceOwner()
    {
        var identity = new ClaimsIdentity([
            new Claim("api_key_id", "00000000-0000-0000-0000-000000000017"),
            new Claim("permission", "agent:workspace"), new Claim(ClaimTypes.Role, "admin")
        ], AuthenticationExtensions.ApiKeyScheme);
        CanonicalSecurityActor.StampAuthorityClaims(identity);
        var principal = new ClaimsPrincipal(identity);
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        var store = Substitute.For<IWorkflowPackageStore>();
        store.GetVersionAsync(Reference.Id, 2, Arg.Any<CancellationToken>()).Returns(Version("human-owner"));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-1");
        using var services = new ServiceCollection().AddSingleton(store).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1", TargetId = "target-1", TenantId = "tenant-1",
            OwnerId = "human-owner", Members = ["human-owner", actor]
        };

        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference)).Should().BeFalse();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference, workspace: document)).Should().BeTrue();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference, workspace: document with { Members = ["human-owner"] })).Should().BeFalse();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference, workspace: document with { OwnerId = "other-owner" })).Should().BeFalse();
        identity.RemoveClaim(identity.FindFirst("permission")!);
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, Reference, workspace: document)).Should().BeFalse();
    }

    [UnitTest]
    public void WorkflowVersionIsCapturedAndInvalidOrMixedTaskReferencesAreRejected()
    {
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1", TargetId = "target-1", OwnerId = "human-owner", Members = ["human-owner"]
        };
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], Reference));
        document = AgentWorkspaceState.AddTurn(document, "human-owner", new("turn-1", "tab-1", 1, "Explain this process"));
        document = AgentWorkspaceState.Update(document, new("tab-1", 1, "target-1", [], Reference with { Revision = 3 }));
        document.Turns["turn-1"].Context.Task.Should().Be(Reference);
        document.Turns["turn-1"].Draft.Should().BeNull();
        foreach (var invalid in new[]
                 {
                     Reference with { ContentHash = null }, Reference with { ContentHash = "model-says-valid" },
                     Reference with { Revision = 0 }, Reference with { Revision = long.MaxValue }, Reference with { PackageKey = "map-1" },
                     Reference with { Kind = "local-process-run" }
                 })
        {
            AgentWorkspaceState.IsValidTaskReference(invalid).Should().BeFalse();
        }
    }

    private static WorkflowPackageVersion Version(string owner) => new()
    {
        PackageId = Reference.Id, Version = 2, SchemaVersion = "workflow-package.v1", PackageHash = Hash,
        Graph = new() { Nodes = [] }, Validation = WorkflowPackageValidationResult.Success(Hash),
        CreatedAt = DateTimeOffset.UtcNow, Ownership = new() { OwnerActorId = owner, TenantId = "tenant-1" }
    };
}
