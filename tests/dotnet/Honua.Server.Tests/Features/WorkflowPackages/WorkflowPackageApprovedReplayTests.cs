// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Operations;
using Honua.Server.Features.WorkflowPackages;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.WorkflowPackages;

/// <summary>Real sealed replay authorization and service actuation; transport inbox journeys are separate integration evidence.</summary>
public sealed class WorkflowPackageApprovedReplayTests
{
    [UnitTest]
    public async Task ValidSealedReplay_PreservesNonDurableDeletion_ButRefusesAgentDurableCapture()
    {
        var fixture = await Fixture.Create();
        var ambient = new HttpContextAccessor { HttpContext = fixture.Approver };
        var replay = await WorkflowPackageApprovedReplay.VerifyAsync(fixture.Services, fixture.Request, fixture.Context, CancellationToken.None);
        ambient.HttpContext.Should().BeSameAs(fixture.Approver);
        replay.Principal.HasClaim("permission", "agent:workspace").Should().BeTrue();
        // Raw stored policy claims are not a source of authenticated authority.
        // Durable work stays refused until its credential-aware RLS bridge is available.
        replay.Principal.HasClaim("rls-group", "original-narrow-group").Should().BeFalse();
        var durableCapture = () => new WorkflowAuthorSecurityContextCapturer(approvedReplay: replay)
            .CaptureAsync(replay.Principal, CancellationToken.None);
        await durableCapture.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        replay.Principal.IsInRole("admin").Should().BeTrue("the agent role cannot exempt the key from this proof");
        var deleted = await replay.CreateService(fixture.Services).DeletePublicationAsync("publication", replay.Principal, CancellationToken.None);
        deleted.Should().NotBeNull();
        (await fixture.Packages.GetPublicationAsync("publication")).Should().BeNull();
        ambient.HttpContext.Should().BeSameAs(fixture.Approver);
    }

    [UnitTest]
    public async Task HumanReplacementAfterReplayVerification_IsRefusedInsideMutationLock()
    {
        var fixture = await Fixture.Create();
        var replay = await WorkflowPackageApprovedReplay.VerifyAsync(fixture.Services, fixture.Request, fixture.Context, CancellationToken.None);
        var original = (await fixture.Packages.GetPublicationAsync("publication"))!;
        await fixture.Packages.DeletePublicationAsync("publication");
        var replacement = await fixture.Packages.SavePublicationAsync(original with { CreatedAt = original.CreatedAt.AddMinutes(1), CreatedBy = "different-human" });
        var invoke = () => replay.CreateService(fixture.Services).DeletePublicationAsync("publication", replay.Principal, CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        (await fixture.Packages.GetPublicationAsync("publication")).Should().Be(replacement);
    }

    [UnitTheory]
    [InlineData("status")]
    [InlineData("seal")]
    [InlineData("actor")]
    [InlineData("operation")]
    [InlineData("instance")]
    [InlineData("payload")]
    [InlineData("revoked")]
    [InlineData("grants")]
    [InlineData("membership")]
    [InlineData("publication")]
    public async Task ChangedApprovalAuthorityOrResource_FailsBeforeActuation(string change)
    {
        var fixture = await Fixture.Create();
        var proposal = fixture.Proposal;
        var request = fixture.Request;
        switch (change)
        {
            case "status": proposal = proposal with { Status = OperationProposalStatus.AwaitingApproval }; break;
            case "seal": proposal = proposal with { Plan = proposal.Plan with { Summary = "different reviewed change" } }; break;
            case "actor": proposal = proposal with { RequestedBy = "another-actor" }; break;
            case "operation": proposal = proposal with { OperationId = "another-operation" }; break;
            case "instance": proposal = proposal with { Audit = new() { OperationInstanceId = "another-instance" } }; break;
            case "payload": request = request with { Parameters = new Dictionary<string, string?> { ["sealedPayload"] = "{}" } }; break;
            case "revoked": await fixture.Keys.RevokeAsync(fixture.KeyId, CancellationToken.None); break;
            case "grants":
                var record = await fixture.Keys.GetAsync(fixture.KeyId, CancellationToken.None);
                var changedStore = Substitute.For<IAdminApiKeyStore>();
                changedStore.GetAsync(fixture.KeyId, Arg.Any<CancellationToken>()).Returns(record! with { Permissions = ["admin", "agent:workspace", "additional:grant"] });
                fixture = fixture with { Services = fixture.Rebuild(changedStore) }; break;
            case "membership": fixture.Workspaces.GetAsync("workspace").Returns(fixture.Document with { Members = [] }); break;
            case "publication": await fixture.Packages.SetPublicationStatusAsync("publication", WorkflowPublicationStatus.Disabled); break;
        }
        fixture.Proposals.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(proposal);
        var invoke = () => WorkflowPackageApprovedReplay.VerifyAsync(fixture.Services, request, fixture.Context, CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await fixture.Packages.GetPublicationAsync("publication")).Should().NotBeNull();
    }

    [UnitTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task ChangedLiveRoleMembership_CannotWidenOrRetainStaleAuthorRlsSnapshot(bool active, bool expanded)
    {
        var fixture = await Fixture.Create();
        var membership = Substitute.For<IPrincipalMembershipSource>();
        membership.ResolveMembershipAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(active, expanded ? ["admin", "expanded-data-role"] : []));
        var services = fixture.Rebuild(fixture.Keys, membership);
        var invoke = () => WorkflowPackageApprovedReplay.VerifyAsync(services, fixture.Request, fixture.Context, CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await fixture.Packages.GetPublicationAsync("publication")).Should().NotBeNull();
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StoredCredentialChangedAfterVerification_IsRejectedInsideMutationLock(bool rotate)
    {
        var fixture = await Fixture.Create();
        var replay = await WorkflowPackageApprovedReplay.VerifyAsync(fixture.Services, fixture.Request, fixture.Context, CancellationToken.None);
        if (rotate) await fixture.Keys.RotateAsync(fixture.KeyId, CancellationToken.None);
        else await fixture.Keys.RevokeAsync(fixture.KeyId, CancellationToken.None);
        var invoke = () => replay.RevalidateKeyAndMembershipAsync(CancellationToken.None);
        await invoke.Should().ThrowAsync<GeoprocessingAuthorizationException>();
        (await fixture.Packages.GetPublicationAsync("publication")).Should().NotBeNull();
    }

    [UnitTest]
    public async Task OrdinaryHuman_DurableCaptureRemainsAvailable()
    {
        var human = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "human"), new Claim(ClaimTypes.Role, "admin")], "Human"));
        var context = await new WorkflowAuthorSecurityContextCapturer().CaptureAsync(human, CancellationToken.None);
        context.Claims.Should().Contain(claim => claim.Type == ClaimTypes.Role && claim.Value == "admin");
    }

    private sealed record Fixture
    {
        public required ServiceProvider Services { get; init; }
        public required InMemoryWorkflowPackageStore Packages { get; init; }
        public required InMemoryAdminApiKeyStore Keys { get; init; }
        public required Guid KeyId { get; init; }
        public required IOperationProposalStore Proposals { get; init; }
        public required IAgentWorkspaceStore Workspaces { get; init; }
        public required AgentWorkspaceDocument Document { get; init; }
        public required OperationProposal Proposal { get; init; }
        public required OperationRequest Request { get; init; }
        public required OperationPolicyContext Context { get; init; }
        public required DefaultHttpContext Approver { get; init; }

        public ServiceProvider Rebuild(IAdminApiKeyStore keys, IPrincipalMembershipSource? membership = null)
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("public");
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>()).Returns(AuthorizationResult.Success());
            var services = new ServiceCollection().AddSingleton(Proposals).AddSingleton(Workspaces)
                .AddSingleton<IAdminApiKeyStore>(keys).AddSingleton<IWorkflowPackageStore>(Packages)
                .AddSingleton(tenant).AddSingleton(authorization).AddSingleton(TimeProvider.System)
                .AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["AgentWorkspace:ReleaseId"] = "release" }).Build())
                .AddSingleton(new WorkflowPackageService(Packages, null!, null!, TimeProvider.System, NullLogger<WorkflowPackageService>.Instance))
                .AddSingleton<IPrincipalMembershipSource>(membership ?? Substitute.For<IPrincipalMembershipSource>());
            return services.BuildServiceProvider();
        }

        private static ITenantContext Tenant()
        {
            var tenant = Substitute.For<ITenantContext>();
            tenant.TenantId.Returns("public");
            return tenant;
        }

        public static async Task<Fixture> Create()
        {
            var keys = new InMemoryAdminApiKeyStore();
            var created = await keys.CreateAsync("original-agent", ["admin", "agent:workspace"], null, "human", CancellationToken.None);
            var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
            ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("rls-group", "original-narrow-group"));
            var actor = CanonicalSecurityActor.Resolve(principal)!.ActorId;
            var owner = "human:owner";
            var packages = new InMemoryWorkflowPackageStore();
            var graph = new WorkflowGraph { Nodes = [] };
            var hash = WorkflowPackageGraphHasher.Compute(graph);
            await packages.SavePackageAsync(new WorkflowPackage
            {
                PackageId = "package",
                Name = "Package",
                Graph = graph,
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
                Ownership = new() { OwnerActorId = owner, TenantId = "public" },
            });
            await packages.CreateVersionAsync("package", hash, graph, WorkflowPackageValidationResult.Success(hash), actor);
            var publication = await packages.SavePublicationAsync(new WorkflowPublication
            {
                PublicationId = "publication",
                PackageId = "package",
                PackageVersion = 1,
                PackageHash = hash,
                Target = WorkflowPublicationTarget.Job,
                Status = WorkflowPublicationStatus.Active,
                Eligibility = WorkflowPackageValidationResult.Success(hash),
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
            var context = new AgentWorkspaceContext("workspace", "target", "view", 1, [], null, "public");
            var document = new AgentWorkspaceDocument
            {
                WorkspaceId = "workspace",
                TargetId = "target",
                OwnerId = owner,
                TenantId = "public",
                Members = [actor, owner],
                Turns = new() { ["turn"] = new("turn", actor, "Remove this exact publication", context, null) },
            };
            var workspaces = Substitute.For<IAgentWorkspaceStore>();
            workspaces.GetTargetIdAsync().Returns("target");
            workspaces.GetAsync("workspace").Returns(document);
            using var captureServices = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys).BuildServiceProvider();
            var credential = await AgentCredentialAuthority.CaptureAsync(principal, captureServices, CancellationToken.None);
            credential.Should().NotBeNull();
            var payload = new WorkflowPackageApprovalPayload
            {
                Credential = credential,
                AuthorSecurityContext = new JobSecurityContext(actor, "public",
                    created.Record.Permissions.Select(permission => new JobSecurityClaim("permission", permission)).ToArray())
                { OwnerActorId = actor },
                Intent = new() { Action = "delete", PackageId = "package", Version = 1, PackageHash = hash, PublicationId = "publication" },
                KeyId = created.Record.Id,
                ActorId = actor,
                Permissions = created.Record.Permissions.ToArray(),
                TenantId = "public",
                WorkspaceId = "workspace",
                TurnId = "turn",
                OwnerActorId = owner,
                TargetId = "target",
                ReleaseId = "release",
                PublicationFingerprint = WorkflowPackageApprovedReplay.Fingerprint(publication),
            };
            var serialized = JsonSerializer.Serialize(payload, WorkflowPackageGovernanceJsonContext.Default.WorkflowPackageApprovalPayload);
            var plan = new OperationProposalPlan { Summary = "Remove publication", ExecutionPayload = serialized };
            var proposal = new OperationProposal
            {
                ProposalId = "proposal",
                OperationId = WorkflowPackageGovernanceOperation.OperationId,
                Kind = Honua.Core.Features.Guardrails.Domain.OperationClass.AdminConfigChange,
                Status = OperationProposalStatus.Executing,
                RequestedBy = actor,
                TenantId = "public",
                Plan = plan,
                SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
                Audit = new() { OperationInstanceId = "instance" },
                CreatedAt = DateTimeOffset.UnixEpoch,
                UpdatedAt = DateTimeOffset.UnixEpoch,
            };
            var proposals = Substitute.For<IOperationProposalStore>();
            proposals.GetAsync("proposal", Arg.Any<CancellationToken>()).Returns(proposal);
            var fixture = new Fixture
            {
                Services = null!,
                Packages = packages,
                Keys = keys,
                KeyId = created.Record.Id,
                Proposals = proposals,
                Workspaces = workspaces,
                Document = document,
                Proposal = proposal,
                Request = new() { OperationId = WorkflowPackageGovernanceOperation.OperationId, Parameters = new Dictionary<string, string?> { ["sealedPayload"] = serialized } },
                Context = new() { PrincipalId = actor, TenantId = "public", OperationInstanceId = "instance", ApprovedProposalId = "proposal", ApprovedPlanHash = proposal.SealedPlanHash },
                Approver = new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "distinct-human")], "Human")) },
            };
            return fixture with { Services = fixture.Rebuild(keys) };
        }
    }
}
