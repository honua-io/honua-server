// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class LocalProcessWorkspaceContextTests
{
    private static readonly string ProjectId = new('a', 32);
    private static readonly string Digest = new('b', 64);
    private static readonly LocalProcessSnapshot Snapshot = new(ProjectId, new('c', 32), Digest, "sha256:runtime", 1);

    [UnitTest]
    public async Task CreateTurn_FreezesSourceOnceAndRetryRetainsSnapshotAfterEdits()
    {
        using var fixture = new Fixture();
        var request = new CreateAgentTurn("turn", "view", 1, "Fix this process");
        var first = await AgentWorkspaceEndpoints.CreateTurn(fixture.Context, fixture.Store, request);
        Assert.Equal(Snapshot, Assert.IsType<AgentTurnResponse>(((IValueHttpResult)first).Value).LocalProcessSnapshot);
        fixture.Identity = fixture.Identity with { Revision = 2, SourceDigest = new('d', 64) };
        var retry = await AgentWorkspaceEndpoints.CreateTurn(fixture.Context, fixture.Store, request);
        Assert.Equal(Snapshot, Assert.IsType<AgentTurnResponse>(((IValueHttpResult)retry).Value).LocalProcessSnapshot);
        await fixture.Gateway.Received(1).SnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(),
            new LocalProcessSnapshotRequest(ProjectId, 1, Digest), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task CreateTurn_RejectsSourceChangedBeforeCapture()
    {
        using var fixture = new Fixture();
        fixture.Identity = fixture.Identity with { Revision = 2 };
        await Assert.ThrowsAsync<AgentWorkspaceConflictException>(() => AgentWorkspaceEndpoints.CreateTurn(
            fixture.Context, fixture.Store, new("turn", "view", 1, "Run this")));
        await fixture.Gateway.DidNotReceive().SnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(),
            Arg.Any<LocalProcessSnapshotRequest>(), Arg.Any<CancellationToken>());
        Assert.Empty(fixture.Document.Turns);
    }

    [UnitTest]
    public async Task TurnRead_RejectsMissingOrReplacedRetainedSnapshot()
    {
        using var fixture = new Fixture();
        var turn = fixture.AddTurn();
        Assert.True(await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(fixture.Context, fixture.Document, turn));
        fixture.Gateway.GetSnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Snapshot.SnapshotId, Arg.Any<CancellationToken>())
            .Returns(Snapshot with { SnapshotDigest = new('d', 64) });
        Assert.False(await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(fixture.Context, fixture.Document, turn));
        Assert.False(await LocalProcessWorkspaceAuthorization.CanReadTurnAsync(fixture.Context, fixture.Document, turn with { LocalProcessSnapshot = null }));
    }

    [UnitTest]
    public async Task SourceTool_DefaultsToFrozenBytesAndExplicitCurrentDoesNotRetarget()
    {
        using var fixture = new Fixture(agent: true);
        fixture.AddTurn();
        fixture.Identity = fixture.Identity with { Revision = 9, SourceDigest = new('d', 64) };
        fixture.Gateway.ReadSnapshotSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Snapshot.SnapshotId, "process.py", Arg.Any<CancellationToken>())
            .Returns(new LocalProcessSourceFile("process.py", "old source", Digest));
        fixture.Gateway.ReadSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), "process.py", Arg.Any<CancellationToken>())
            .Returns(new LocalProcessSourceFile("process.py", "new source", new('d', 64)));
        var tool = LocalProcessWorkspaceTool.All.Single(value => value.Name.EndsWith("_read_source", StringComparison.Ordinal));
        using var oldArgs = JsonDocument.Parse("""{"workspaceId":"workspace","turnId":"turn","path":"process.py"}""");
        var old = await tool.InvokeAsync(fixture.Context, oldArgs.RootElement, CancellationToken.None);
        Assert.False(old.IsError);
        Assert.Contains("old source", old.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        await fixture.Gateway.DidNotReceive().ReadSourceAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        using var currentArgs = JsonDocument.Parse("""{"workspaceId":"workspace","turnId":"turn","path":"process.py","current":true}""");
        var current = await tool.InvokeAsync(fixture.Context, currentArgs.RootElement, CancellationToken.None);
        Assert.False(current.IsError);
        Assert.Contains("new source", current.StructuredContent!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(Snapshot, fixture.Document.Turns["turn"].LocalProcessSnapshot);
    }

    [UnitTest]
    public async Task NativeBinding_CannotSelectAnotherWorkspaceOrTurn()
    {
        using var fixture = new Fixture(agent: true);
        fixture.AddTurn();
        fixture.Context.Request.Headers["X-Honua-Workspace"] = "foreign";
        var denied = await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() =>
            LocalProcessWorkspaceAuthorization.AuthorizeAsync(fixture.Context, "workspace", "turn", true));
        Assert.Equal(403, denied.StatusCode);
        fixture.Context.Request.Headers["X-Honua-Workspace"] = "workspace";
        fixture.Context.Request.Headers["X-Honua-Turn"] = "foreign-turn";
        await Assert.ThrowsAsync<LocalProcessWorkspaceException>(() =>
            LocalProcessWorkspaceAuthorization.AuthorizeAsync(fixture.Context, "workspace", "turn", true));
    }

    [UnitTest]
    public void TrustedAccess_RejectsForeignTenantAndNonownerHuman()
    {
        using var fixture = new Fixture();
        Assert.NotNull(LocalProcessWorkspaceAuthorization.TrustedAccess(fixture.Context, fixture.Document));
        Assert.Null(LocalProcessWorkspaceAuthorization.TrustedAccess(fixture.Context, fixture.Document with { TenantId = "foreign" }));
        Assert.Null(LocalProcessWorkspaceAuthorization.TrustedAccess(fixture.Context, fixture.Document with { OwnerId = "another-human" }));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        public DefaultHttpContext Context { get; }
        public IAgentWorkspaceStore Store { get; } = Substitute.For<IAgentWorkspaceStore>();
        public ILocalProcessWorkspaceGateway Gateway { get; } = Substitute.For<ILocalProcessWorkspaceGateway>();
        public AgentWorkspaceDocument Document { get; private set; }
        public LocalProcessProjectIdentity Identity { get; set; }

        public Fixture(bool agent = false)
        {
            var human = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Bearer"));
            var owner = CanonicalSecurityActor.Resolve(human)!.ActorId;
            var principal = human;
            if (agent)
            {
                var identity = new ClaimsIdentity([new("api_key_id", "00000000-0000-0000-0000-000000000017"),
                    new("permission", "agent:workspace"), new("permission", "admin:write")], AuthenticationExtensions.ApiKeyScheme);
                CanonicalSecurityActor.StampAuthorityClaims(identity);
                principal = new(identity);
            }
            var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
            Document = new()
            {
                WorkspaceId = "workspace", TargetId = "target", OwnerId = owner, TenantId = "tenant", Members = [owner, actor],
                Task = new(ProjectId, 1, "local-process-workspace", ContentHash: Digest),
            };
            Document.Views["view"] = [new("workspace", "target", "view", 1, [], Document.Task, "tenant")];
            Identity = new(ProjectId, 1, Digest, Snapshot.Image, new(owner, "tenant", "target", "workspace"));
            Store.GetAsync("workspace").Returns(_ => Document);
            Store.GetTargetIdAsync().Returns("target");
            Store.SaveAsync(Arg.Any<AgentWorkspaceDocument>()).Returns(call => { Document = call.Arg<AgentWorkspaceDocument>(); return true; });
            Gateway.IsConfigured.Returns(true);
            Gateway.GetIdentityAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<CancellationToken>()).Returns(_ => Identity);
            Gateway.SnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Arg.Any<LocalProcessSnapshotRequest>(), Arg.Any<CancellationToken>()).Returns(Snapshot);
            Gateway.GetSnapshotAsync(Arg.Any<LocalProcessWorkspaceAccess>(), Snapshot.SnapshotId, Arg.Any<CancellationToken>()).Returns(Snapshot);
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("tenant");
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
            var turns = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
            turns.AuthorizeAsync(principal, "workspace", "turn", Arg.Any<CancellationToken>())
                .Returns(new AgentWorkspaceTurnAuthorization("workspace", "turn", "target", "tenant", "release", owner));
            _services = new ServiceCollection().AddSingleton(Store).AddSingleton(Gateway).AddSingleton(tenant)
                .AddSingleton(authorization).AddSingleton(turns).BuildServiceProvider();
            Context = new() { User = principal, RequestServices = _services };
            Context.Request.RouteValues["workspaceId"] = "workspace";
            Context.Request.Path = "/mcp";
        }

        public AgentWorkspaceTurn AddTurn()
        {
            Document = AgentWorkspaceState.AddTurn(Document, Document.OwnerId, new("turn", "view", 1, "Fix this process"));
            var turn = Document.Turns["turn"] with { LocalProcessSnapshot = Snapshot };
            Document.Turns["turn"] = turn;
            return turn;
        }

        public void Dispose() => _services.Dispose();
    }
}
