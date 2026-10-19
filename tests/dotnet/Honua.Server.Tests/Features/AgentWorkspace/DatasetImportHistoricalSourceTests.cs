// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Source ownership kernel only; actual Host/credential checks are exercised by
// the accepted historical import tests. No retained bytes or daemon is simulated.
public sealed class DatasetImportHistoricalSourceTests
{
    [UnitTheory]
    [InlineData("unchanged", true)]
    [InlineData("owner", false)]
    [InlineData("project", false)]
    [InlineData("workspace", false)]
    [InlineData("membership", false)]
    public async Task CurrentProjectRightsSurviveRunCleanupButNeverOwnershipRevocation(string change, bool allowed)
    {
        var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "human")], "oidc"));
        var actor = CanonicalSecurityActor.Resolve(human)!.ActorId;
        var workspace = new AgentWorkspaceDocument { WorkspaceId = "workspace", TargetId = "target", OwnerId = actor, Members = [actor] };
        if (change == "membership") workspace = workspace with { Members = [] };
        var gateway = Substitute.For<ILocalProcessWorkspaceGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.GetIdentityAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<CancellationToken>()).Returns(new LocalProcessProjectIdentity(
            change == "project" ? "other" : "project", 99, new string('b', 64), "image",
            new(change == "owner" ? "other" : actor, null, "target", change == "workspace" ? "other" : "workspace")));
        using var services = new ServiceCollection().AddSingleton(gateway).BuildServiceProvider();
        var source = new DatasetImportSource("local-process-artifact", "roads.geojson", 100, new string('a', 64),
            new("project", "old-snapshot", new string('c', 64), "old-cleaned-run", "out/roads.geojson"));
        Assert.Equal(allowed, await DatasetImportTaskProvider.HistoricalSourceAuthorizedAsync(services, human, null, workspace, source, default));
        await gateway.DidNotReceive().GetRunAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await gateway.DidNotReceive().ReadArtifactAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
