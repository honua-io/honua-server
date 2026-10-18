// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceStudioTaskTests
{
    [UnitTest]
    public async Task StudioTaskRequiresCanonicalPackageGenerationAndCurrentDraftAuthorization()
    {
        var id = Guid.NewGuid();
        var reference = new AgentTaskReference(id.ToString("D"), 3, "studio-composition", "map-1");
        var draft = new StudioPackageDraft
        {
            DraftId = id,
            ItemId = Guid.NewGuid(),
            PackageKey = "map-1",
            Generation = 3,
            OwnerId = "owner-1",
            TenantId = "tenant-1",
            Family = StudioPackageFamily.Map,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Envelope = new() { Family = StudioPackageFamily.Map, SchemaVersion = "1" },
        };
        var lifecycle = Substitute.For<IStudioPackageLifecycleService>();
        lifecycle.GetDraftAsync(id, Arg.Any<CancellationToken>()).Returns(draft);
        var access = Substitute.For<IStudioAuthorizationService>();
        var audit = Substitute.For<IAuditLog>();
        access.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), StudioAuthorizationOperation.ReadDraft,
            "owner-1", "tenant-1", false, id.ToString("D"), Arg.Any<CancellationToken>()).Returns(StudioAuthorizationDecision.Allow());
        using var services = new ServiceCollection().AddSingleton(lifecycle)
            .AddSingleton(new StudioEndpointAuthorization(access, audit, TimeProvider.System)).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, reference, true)).Should().BeTrue();
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, reference with { PackageKey = "different-map" }, true)).Should().BeFalse();
        lifecycle.GetDraftAsync(id, Arg.Any<CancellationToken>()).Returns(draft with { Generation = 4 });
        var changed = () => AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, reference, true);
        await changed.Should().ThrowAsync<AgentWorkspaceConflictException>();
        // Existing submitted turns retain their generation but must still pass today's ACL.
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, reference)).Should().BeTrue();
        access.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<string?>(), StudioAuthorizationOperation.ReadDraft,
            "owner-1", "tenant-1", false, id.ToString("D"), Arg.Any<CancellationToken>()).Returns(StudioAuthorizationDecision.Deny("revoked", "Access revoked"));
        (await AgentWorkspaceResourceAuthorization.CanReadTaskAsync(context, reference)).Should().BeFalse();
        await audit.Received().RecordAsync(Arg.Is<AuditEvent>(entry => entry.ResourceId == id.ToString("D") && entry.Outcome == AuditOutcome.Denied), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void CapturedStudioTaskDoesNotFallBackToAWorkspaceFormOrLaterSelection()
    {
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            OwnerId = "owner-1",
            Members = ["owner-1"],
        };
        var first = new AgentTaskReference(Guid.NewGuid().ToString("D"), 3, "studio-composition", "map-1");
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], first, "studio"));
        document = AgentWorkspaceState.AddTurn(document, "owner-1", new("turn-1", "tab-1", 1, "Style this map"));
        var second = first with { Id = Guid.NewGuid().ToString("D"), PackageKey = "map-2", Revision = 0 };
        document = AgentWorkspaceState.Update(document, new("tab-1", 1, "target-1", [], second, "studio"));
        document.Turns["turn-1"].Context.Task.Should().Be(first);
        document.Turns["turn-1"].Draft.Should().BeNull();
        document.Task.Should().Be(second);
    }
}
