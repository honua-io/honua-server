// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class OperationHumanApprovalAuthorityTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VerifyAsync_AuthorizedHuman_AllowsRequesterAndAnotherPerson(bool requesterApproves)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var requester = requesterApproves ? CanonicalSecurityActor.Resolve(human.Principal)!.ActorId : "agent:requester";
        var proposal = OperationHumanApprovalTestHost.Proposal(requester);
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);

        var evidence = await host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));

        evidence.Version.Should().Be(OperationHumanApprovalEvidence.CurrentVersion);
        evidence.ActorId.Should().Be(CanonicalSecurityActor.Resolve(human.Principal)!.ActorId);
        evidence.Channel.Should().Be(OperationHumanApprovalEvidence.InteractiveOidcChannel);
        review.TenantId.Should().Be(proposal.TenantId);
        evidence.PlanHash.Should().Be(proposal.SealedPlanHash);
        evidence.ProposalVersion.Should().Be(proposal.Version);
        evidence.CapabilityReference.Should().HaveLength(64);
        evidence.SessionReference.Should().HaveLength(64).And.NotBe(human.SessionId);
        review.ExpiresAt.Should().BeOnOrBefore(host.Clock.GetUtcNow().AddMinutes(5));
    }

    [UnitTheory]
    [InlineData("bearer")]
    [InlineData("key")]
    [InlineData("mcp")]
    [InlineData("query-token")]
    [InlineData("query-key")]
    [InlineData("esri")]
    [InlineData("embed")]
    [InlineData("token-header")]
    public async Task OpenReviewAsync_NonHumanTransportWithLiveCookie_RefusesCapability(string transport)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        switch (transport)
        {
            case "bearer": host.Context.Request.Headers.Authorization = "Bearer noninteractive"; break;
            case "key": host.Context.Request.Headers["X-API-Key"] = "noninteractive"; break;
            case "mcp": host.Context.Request.Path = "/mcp"; break;
            case "query-key": host.Context.Request.QueryString = new("?key=noninteractive"); break;
            case "esri": host.Context.Request.Headers["X-Esri-Authorization"] = "noninteractive"; break;
            case "embed": host.Context.Request.Headers["X-Honua-Embed-Key"] = "noninteractive"; break;
            case "token-header": host.Context.Request.Headers["X-Honua-Token"] = "noninteractive"; break;
            case "query-token": host.Context.Request.QueryString = new("?token=noninteractive"); break;
        }

        var open = () => host.Authority.OpenReviewAsync(OperationHumanApprovalTestHost.Proposal(), host.Context);

        await open.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTest]
    public async Task OpenReviewAsync_UnverifiedOriginWithInteractiveClaims_RefusesCapability()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync(origin: AdminAuthSessionOrigin.Unknown);
        host.ReviewRequest(human);

        var open = () => host.Authority.OpenReviewAsync(OperationHumanApprovalTestHost.Proposal(), host.Context);

        await open.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTheory]
    [InlineData("version")]
    [InlineData("proposal")]
    [InlineData("tenant")]
    [InlineData("requester")]
    [InlineData("scope")]
    [InlineData("operation-instance")]
    [InlineData("plan")]
    [InlineData("policy")]
    public async Task VerifyAsync_ReviewBindingChanged_RefusesDecision(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var proposal = OperationHumanApprovalTestHost.Proposal();
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);
        var changed = ChangedProposal(proposal, change);

        var verify = () => host.Authority.VerifyAsync(changed, OperationHumanApprovalTestHost.Approval(human));

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTheory]
    [InlineData("tampered")]
    [InlineData("oversized")]
    [InlineData("duplicate")]
    public async Task VerifyAsync_InvalidCapability_RefusesDecision(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var proposal = OperationHumanApprovalTestHost.Proposal();
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);
        host.Context.Request.Headers[OperationHumanApprovalAuthority.CapabilityHeader] = change switch
        {
            "tampered" => review.Capability + "tampered",
            "oversized" => new string('x', 16385),
            _ => new StringValues([review.Capability, review.Capability]),
        };

        var verify = () => host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTest]
    public async Task VerifyAsync_SameActorDifferentSession_RefusesDecision()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var proposal = OperationHumanApprovalTestHost.Proposal();
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        var otherSession = await host.CreateHumanAsync();
        host.ApproveRequest(otherSession, review);

        var verify = () => host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(otherSession));

        await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTheory]
    [InlineData("expired")]
    [InlineData("logout")]
    [InlineData("permission")]
    [InlineData("mcp")]
    [InlineData("resolved")]
    public async Task VerifyAsync_AuthorityLostAfterReview_RefusesDecision(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var proposal = OperationHumanApprovalTestHost.Proposal();
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);
        switch (change)
        {
            case "expired": host.Clock.Advance(TimeSpan.FromMinutes(6)); break;
            case "logout": await host.Sessions.RemoveAuthenticatedSessionAsync(human.SessionId, default); break;
            case "permission":
                host.Permissions.AuthorizeAsync(default!, default!, default!, default, default, default, default)
                    .ReturnsForAnyArgs(PermissionDecision.RequiresAuthentication());
                break;
            case "mcp": host.Context.Request.Path = "/mcp"; break;
            case "resolved": proposal = proposal with { Status = OperationProposalStatus.Succeeded }; break;
        }

        var verify = () => host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));

        if (change == "resolved")
            await verify.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no longer awaiting human approval*");
        else
            await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    [UnitTest]
    public async Task OpenReviewAsync_LegacyPendingProposal_RequiresFreshProposal()
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        var legacy = OperationHumanApprovalTestHost.Proposal() with { Initiation = null };

        var open = () => host.Authority.OpenReviewAsync(legacy, host.Context);

        await open.Should().ThrowAsync<InvalidOperationException>().WithMessage("*fresh proposal*");
    }

    [UnitTheory]
    [InlineData("shared", true)]
    [InlineData("wrong-key", false)]
    [InlineData("wrong-audience", false)]
    public async Task VerifyAsync_IndependentProviders_RequiresSharedKeysAndAudience(string destination, bool accepted)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "honua-human-approval-" + Guid.NewGuid().ToString("N")));
        try
        {
            var sharedSessions = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
            using var first = new OperationHumanApprovalTestHost(DataProtectionProvider.Create(directory), sharedSessions);
            var human = await first.CreateHumanAsync();
            var proposal = OperationHumanApprovalTestHost.Proposal();
            first.ReviewRequest(human);
            var review = await first.Authority.OpenReviewAsync(proposal, first.Context);
            var keys = destination == "wrong-key" ? new EphemeralDataProtectionProvider() : DataProtectionProvider.Create(directory);
            using var second = new OperationHumanApprovalTestHost(keys, sharedSessions,
                destination == "wrong-audience" ? "https://other.example.com" : "https://server.example.com");
            second.ApproveRequest(human, review);

            var verify = () => second.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));

            if (accepted) (await verify()).ActorId.Should().Be(CanonicalSecurityActor.Resolve(human.Principal)!.ActorId);
            else await verify.Should().ThrowAsync<HumanApprovalRequiredException>();
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [UnitTheory]
    [InlineData(TenantContextSource.Default, true)]
    [InlineData(TenantContextSource.Header, false)]
    [InlineData(TenantContextSource.Claim, false)]
    public async Task OpenReviewAsync_GlobalProposal_OnlyIgnoresTheAnonymousDefaultTenant(
        TenantContextSource source, bool accepted)
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        host.ReviewRequest(human);
        host.Context.RequestServices.GetRequiredService<RequestTenantContext>().Set("public", source);
        var original = OperationHumanApprovalTestHost.Proposal();
        var proposal = original with { TenantId = null, Initiation = original.Initiation! with { TenantId = null } };

        var review = () => host.Authority.OpenReviewAsync(proposal, host.Context);

        if (accepted)
        {
            var capability = await review();
            host.ApproveRequest(human, capability);
            host.Context.RequestServices.GetRequiredService<RequestTenantContext>().Set("public", source);
            var decision = OperationHumanApprovalTestHost.Approval(human) with { TenantId = "public" };
            (await host.Authority.VerifyAsync(proposal, decision)).ActorId.Should().Be(decision.ApprovedBy);
        }
        else await review.Should().ThrowAsync<HumanApprovalRequiredException>();
    }

    private static OperationProposal ChangedProposal(OperationProposal proposal, string change)
    {
        var plan = proposal.Plan with { Summary = "A different operation plan" };
        var hash = OperationApprovalPlanSeal.Compute(plan);
        var policy = proposal.GuardrailDecision! with { Source = "changed-policy" };
        return change switch
        {
            "version" => proposal with { Version = proposal.Version + 1 },
            "proposal" => proposal with { ProposalId = "other-proposal" },
            "tenant" => proposal with { TenantId = "tenant-b", Initiation = proposal.Initiation! with { TenantId = "tenant-b" } },
            "requester" => proposal with { RequestedBy = "another-agent", Initiation = proposal.Initiation! with { PrincipalId = "another-agent" } },
            "operation-instance" => proposal with { Audit = proposal.Audit with { OperationInstanceId = "another-instance" } },
            "scope" => proposal with { ScopeGoverned = true, RecognizedScopes = ["honua.mcp.operate"] },
            "plan" => proposal with { Plan = plan, SealedPlanHash = hash, Initiation = proposal.Initiation! with { PlanHash = hash } },
            _ => proposal with { GuardrailDecision = policy, Initiation = proposal.Initiation! with { PolicyRevision = OperationGateway.ComputePolicyRevision(policy) } },
        };
    }
}
