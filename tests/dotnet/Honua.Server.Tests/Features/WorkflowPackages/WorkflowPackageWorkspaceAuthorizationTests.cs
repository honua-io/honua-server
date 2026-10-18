// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.AgentWorkspace.Abstractions;
using Honua.Core.Features.AgentWorkspace.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Security;
using Honua.Server.Features.WorkflowPackages;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed class WorkflowPackageWorkspaceAuthorizationTests
{
    private const string Human = "bearer:subject:issuer:workspace-human";
    private const string Tenant = "public";

    [UnitTest]
    public async Task AdminWorkspaceKey_CannotReadOrReplaceForeignTenantOwnerOrUnknownPackages_ThroughLegacyOverloads()
    {
        var (service, store, context, _, principal) = Harness();
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        await Seed(store, "own", actor, Tenant);
        await Seed(store, "foreign", Human, Tenant);
        await Seed(store, "tenant", actor, "another-tenant");
        await Seed(store, "legacy", null, null);
        (await service.ListPackagesAsync(CancellationToken.None)).Select(package => package.PackageId).Should().Equal("own");
        foreach (var id in new[] { "foreign", "tenant", "legacy" })
        {
            (await service.GetPackageAsync(id, CancellationToken.None)).Should().BeNull();
            (await service.GetVersionAsync(id, 1, CancellationToken.None)).Should().BeNull();
            (await service.ListVersionsAsync(id, CancellationToken.None)).Should().BeEmpty();
            var replace = () => service.SavePackageAsync(Request(id), principal, CancellationToken.None);
            await replace.Should().ThrowAsync<KeyNotFoundException>();
            var version = () => service.CreateVersionAsync(id, principal, CancellationToken.None);
            await version.Should().ThrowAsync<KeyNotFoundException>();
        }
        context.User.IsInRole("admin").Should().BeTrue("admin role must not exempt an agent from ownership checks");
        (await store.GetPackageAsync("foreign"))!.Name.Should().Be("foreign");
    }

    [UnitTest]
    public async Task SameActorCopyCannotRemoveWorkspaceRestriction_AndEveryWriteChecksActualAuthorization()
    {
        var (service, store, context, _, principal) = Harness();
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        await Seed(store, "own", actor, Tenant);
        await Seed(store, "foreign", Human, Tenant);
        var unmarkedCopy = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("api_key_id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), new Claim(ClaimTypes.Role, "admin"),
        ], "ApiKey"));
        (await service.GetPackageAsync("foreign", unmarkedCopy, CancellationToken.None)).Should().BeNull();
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => ((HttpContext)call.ArgAt<object>(1)).Request.Method == HttpMethods.Post
                ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        (await service.GetPackageAsync("own", principal, CancellationToken.None)).Should().NotBeNull();
        var write = () => service.SavePackageAsync(Request("own"), principal, CancellationToken.None);
        await write.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await store.GetPackageAsync("own"))!.Name.Should().Be("own");
    }

    [UnitTest]
    public async Task VerifiedWorkspaceDelegation_AttributesHumanOwnerButRetainsActualAgentAuthor_AndRechecksRevocation()
    {
        var (service, store, context, turns, principal) = Harness();
        context.Request.Headers["X-Honua-Workspace"] = "workspace-a";
        context.Request.Headers["X-Honua-Turn"] = "turn-a";
        var binding = new AgentWorkspaceTurnAuthorization("workspace-a", "turn-a", "target-a", Tenant, "release-a", Human);
        turns.AuthorizeAsync(principal, "workspace-a", "turn-a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentWorkspaceTurnAuthorization?>(binding),
                Task.FromResult<AgentWorkspaceTurnAuthorization?>(binding),
                Task.FromResult<AgentWorkspaceTurnAuthorization?>(null));
        var saved = await service.SavePackageAsync(Request(null) with
        {
            Metadata = new Dictionary<string, string> { ["ownerActorId"] = "forged-owner", ["tenantId"] = "forged-tenant" },
        }, principal, CancellationToken.None);
        saved.Ownership.Should().Be(new WorkflowPackageOwnership { OwnerActorId = Human, TenantId = Tenant });
        saved.CreatedBy.Should().Be(CanonicalSecurityActor.Resolve(principal)!.ActorId,
            "delegation must retain the actual agent authorizing principal");
        (await service.GetPackageAsync(saved.PackageId, CancellationToken.None)).Should().NotBeNull();
        var revoked = () => service.GetPackageAsync(saved.PackageId, CancellationToken.None);
        await revoked.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await store.GetPackageAsync(saved.PackageId))!.Ownership.Should().Be(saved.Ownership);
    }

    [UnitTest]
    public async Task NativeHeaderBinding_CannotBeRetargetedByExplicitLocator_BeforeAuthorizerCall()
    {
        var (_, _, context, turns, principal) = Harness();
        context.Request.Headers["X-Honua-Workspace"] = "workspace-a";
        context.Request.Headers["X-Honua-Turn"] = "turn-a";
        var authorization = new WorkflowPackageAuthorization(new HttpContextAccessor { HttpContext = context }, TenantContext(), turns);
        var retarget = () => authorization.ResolveAsync(principal, CancellationToken.None, "workspace-b", "turn-b");
        await retarget.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        await turns.DidNotReceiveWithAnyArgs().AuthorizeAsync(default!, default!, default!, default);
    }

    [UnitTest]
    public async Task MissingPolicyOrMissingTrustedWorkspaceOwner_CannotAuthorizeWorkspaceCredential()
    {
        var (_, store, context, turns, principal) = Harness();
        var missing = new WorkflowPackageService(store, null!, null!, TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance);
        var save = () => missing.SavePackageAsync(Request(null), principal, CancellationToken.None);
        await save.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        context.Request.Headers["X-Honua-Workspace"] = "workspace-a";
        context.Request.Headers["X-Honua-Turn"] = "turn-a";
        turns.AuthorizeAsync(principal, "workspace-a", "turn-a", Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization("workspace-a", "turn-a", "target-a", Tenant, "release-a"));
        var authorization = new WorkflowPackageAuthorization(new HttpContextAccessor { HttpContext = context }, TenantContext(), turns);
        var unavailableOwner = () => authorization.ResolveAsync(principal, CancellationToken.None);
        await unavailableOwner.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    [UnitTest]
    public async Task ForeignPublications_AreHiddenBeforeMutationOrRun_AndOwnedLegacyGovernanceCannotExecuteDirectly()
    {
        var (service, store, _, _, principal) = Harness();
        var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
        await Seed(store, "own", actor, Tenant);
        await Seed(store, "foreign", Human, Tenant);
        await Publication(store, "own", "own-publication");
        await Publication(store, "foreign", "foreign-publication");
        (await service.ListPublicationsAsync(null, principal, CancellationToken.None)).Select(item => item.PublicationId)
            .Should().Equal("own-publication");
        (await service.DeletePublicationAsync("foreign-publication", CancellationToken.None)).Should().BeNull();
        (await service.SetPublicationStatusAsync("foreign-publication", WorkflowPublicationStatus.Disabled, CancellationToken.None)).Should().BeNull();
        var run = () => service.RunPublicationAsync("foreign-publication", new RunWorkflowPublicationRequest(), principal, CancellationToken.None);
        await run.Should().ThrowAsync<KeyNotFoundException>();
        var delete = () => service.DeletePublicationAsync("own-publication", CancellationToken.None);
        await delete.Should().ThrowAsync<GeoprocessingApprovalRequiredException>();
        (await store.GetPublicationAsync("own-publication"))!.Status.Should().Be(WorkflowPublicationStatus.Active);
    }

    [UnitTest]
    public async Task BuiltInAuthoring_CreatesDistinctImmutablePackages_WithVerifiedHumanOwnership_WithoutActuation()
    {
        var (_, store, context, turns, principal) = Harness();
        turns.AuthorizeAsync(principal, "workspace-a", "turn-a", Arg.Any<CancellationToken>())
            .Returns(new AgentWorkspaceTurnAuthorization("workspace-a", "turn-a", "target-a", Tenant, "release-a", Human));
        var authorization = new WorkflowPackageAuthorization(new HttpContextAccessor { HttpContext = context }, TenantContext(), turns);
        var registry = Substitute.For<IWorkflowNodeRegistry>();
        registry.GetSnapshotAsync(Arg.Any<CancellationToken>()).Returns(new WorkflowNodeRegistrySnapshot
        {
            RegistryVersion = "fixture",
            GeneratedAt = DateTimeOffset.UnixEpoch,
            Providers = [],
            Nodes = [new WorkflowNodeDefinition
            {
                NodeTypeId = "process:test.echo", ProviderId = "test", RuntimeKind = WorkflowNodeRuntimeKind.Geoprocessing,
                Title = "Echo", Description = "Echo", Category = "test", ParameterSchemas = [],
                CapabilityFlags = new() { Executable = true, SupportsJob = true }, RuntimeHints = new(),
            }],
        });
        var jobs = Substitute.For<IGeoprocessingJobService>();
        jobs.ValidatePlan(Arg.Any<AnalysisPlan>(), principal).Returns(new PlanValidationResult { IsExecutable = true });
        var packages = new WorkflowPackageService(store, registry, jobs, TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance,
            authorSecurityContextCapturer: new WorkflowAuthorSecurityContextCapturer(tenantContext: TenantContext(), packageAuthorization: authorization));
        var workspaces = Substitute.For<IAgentWorkspaceStore>();
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-a",
            TargetId = "target-a",
            OwnerId = Human,
            TenantId = Tenant,
            Members = [Human, CanonicalSecurityActor.Resolve(principal)!.ActorId],
        };
        document = AgentWorkspaceState.Update(document, new("view-a", 0, "target-a", [], null, "console"));
        document = AgentWorkspaceState.AddTurn(document, Human, new("turn-a", "view-a", 1, "Create workflow"));
        var gate = new object();
        workspaces.GetTargetIdAsync().Returns("target-a");
        workspaces.GetAsync("workspace-a").Returns(_ => { lock (gate) return Task.FromResult<AgentWorkspaceDocument?>(document); });
        workspaces.SaveAsync(Arg.Any<AgentWorkspaceDocument>()).Returns(call =>
        {
            lock (gate)
            {
                var next = call.Arg<AgentWorkspaceDocument>();
                if (next.Version != document.Version) return Task.FromResult(false);
                document = next with { Version = next.Version + 1 };
                return Task.FromResult(true);
            }
        });
        var authoring = new WorkflowPackageAuthoring(packages, authorization, workspaces);
        var graph = new WorkflowGraph { Nodes = [new WorkflowNode { NodeId = "echo", NodeTypeId = "process:test.echo" }] };
        var first = await authoring.CreateAsync(principal, "workspace-a", "turn-a", "command-first", "First", graph, CancellationToken.None);
        var second = await authoring.CreateAsync(principal, "workspace-a", "turn-a", "command-edited", "Edited", graph, CancellationToken.None);
        var retries = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new WorkflowPackageAuthoring(packages, authorization, workspaces).CreateAsync(principal, "workspace-a", "turn-a",
                "command-first", "First", graph, CancellationToken.None)));
        retries.Should().OnlyContain(version => version.PackageId == first.PackageId && version.Version == 1);
        (await store.ListVersionsAsync(first.PackageId)).Should().ContainSingle();
        var mismatch = () => authoring.CreateAsync(principal, "workspace-a", "turn-a", "command-first", "Changed name", graph, CancellationToken.None);
        await mismatch.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        var mutable = (await store.GetPackageAsync(first.PackageId))!;
        await store.SavePackageAsync(mutable with { Name = "Human edit", Graph = new() { Nodes = [] } });
        var reconciled = await authoring.CreateAsync(principal, "workspace-a", "turn-a", "command-first", "First", graph, CancellationToken.None);
        reconciled.Should().Be(first);
        (await store.GetPackageAsync(first.PackageId))!.Name.Should().Be("Human edit");
        second.PackageId.Should().NotBe(first.PackageId);
        first.Version.Should().Be(1);
        second.Version.Should().Be(1);
        first.Ownership.Should().Be(new WorkflowPackageOwnership { OwnerActorId = Human, TenantId = Tenant });
        first.CreatedBy.Should().Be(CanonicalSecurityActor.Resolve(principal)!.ActorId);
        (await authoring.ReadAsync(principal, "workspace-a", "turn-a", first.PackageId, 1, CancellationToken.None)).Should().Be(first);
        (await store.ListPublicationsAsync()).Should().BeEmpty();
        // No execution method is invoked: only canonical authorization and plan validation.
        jobs.ReceivedCalls().Should().OnlyContain(call => call.GetMethodInfo().Name == "EnsureCallerAuthorizedAsync" || call.GetMethodInfo().Name == "ValidatePlan");
        turns.AuthorizeAsync(principal, "workspace-a", "turn-a", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AgentWorkspaceTurnAuthorization?>(null));
        var revoked = () => authoring.ReadAsync(principal, "workspace-a", "turn-a", first.PackageId, 1, CancellationToken.None);
        await revoked.Should().ThrowAsync<GeoprocessingAuthorizationException>();
    }

    private static (WorkflowPackageService Service, InMemoryWorkflowPackageStore Store, DefaultHttpContext Context,
        IAgentWorkspaceTurnAuthorizer Turns, ClaimsPrincipal Principal) Harness()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("api_key_id", "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new Claim(ClaimTypes.NameIdentifier, "api-key:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            new Claim(ClaimTypes.Role, "admin"), new Claim("permission", "admin:read"),
            new Claim("permission", "admin:write"), new Claim("permission", "agent:workspace"),
        ], "ApiKey"));
        var tenant = TenantContext();
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var services = new ServiceCollection().AddSingleton(authorization).AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { User = principal, RequestServices = services };
        var turns = Substitute.For<IAgentWorkspaceTurnAuthorizer>();
        var resource = new WorkflowPackageAuthorization(new HttpContextAccessor { HttpContext = context }, tenant, turns);
        var store = new InMemoryWorkflowPackageStore();
        var service = new WorkflowPackageService(store, null!, null!, TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance,
            authorSecurityContextCapturer: new WorkflowAuthorSecurityContextCapturer(tenantContext: tenant, packageAuthorization: resource));
        return (service, store, context, turns, principal);
    }

    private static ITenantContext TenantContext()
    {
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(Tenant);
        return tenant;
    }

    private static SaveWorkflowPackageRequest Request(string? id)
        => new() { PackageId = id, Name = "Updated", Graph = new WorkflowGraph { Nodes = [] } };

    private static async Task Seed(InMemoryWorkflowPackageStore store, string id, string? owner, string? tenant)
    {
        var graph = new WorkflowGraph { Nodes = [] };
        await store.SavePackageAsync(new WorkflowPackage
        {
            PackageId = id,
            Name = id,
            Graph = graph,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Ownership = owner is null ? null : new() { OwnerActorId = owner, TenantId = tenant },
        });
        var hash = WorkflowPackageGraphHasher.Compute(graph);
        await store.CreateVersionAsync(id, hash, graph, WorkflowPackageValidationResult.Success(hash), "fixture");
    }

    private static Task<WorkflowPublication> Publication(InMemoryWorkflowPackageStore store, string package, string id)
        => store.SavePublicationAsync(new WorkflowPublication
        {
            PublicationId = id,
            PackageId = package,
            PackageVersion = 1,
            PackageHash = "fixture-hash",
            Target = WorkflowPublicationTarget.Job,
            Status = WorkflowPublicationStatus.Active,
            Eligibility = WorkflowPackageValidationResult.Success("fixture-hash"),
            CreatedAt = DateTimeOffset.UtcNow,
        });
}
