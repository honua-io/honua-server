// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class OperationHumanApprovalReplayTests
{
    [UnitTheory]
    [InlineData("unchanged", true, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("unchanged", true, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("scope", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("scope", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("actor", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("actor", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("plan", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("plan", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("instance", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("instance", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("initiation", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("initiation", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("missing-proof", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("missing-proof", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("pending", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("pending", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("channel", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("channel", false, (int)AdminAuthSessionOrigin.Builtin)]
    [InlineData("version", false, (int)AdminAuthSessionOrigin.InteractiveOidc)]
    [InlineData("version", false, (int)AdminAuthSessionOrigin.Builtin)]
    public async Task VerifyAsync_ApprovedExecution_RequiresItsExactDurableHumanEvidence(string change, bool expected, int originValue)
    {
        var origin = (AdminAuthSessionOrigin)originValue;
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync(origin: origin);
        var proposal = OperationHumanApprovalTestHost.Proposal(CanonicalSecurityActor.Resolve(human.Principal)!.ActorId);
        proposal = proposal with { Audit = proposal.Audit with { OperationInstanceId = "approved-instance" } };
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);
        var approval = await host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));
        proposal = proposal with
        {
            Status = OperationProposalStatus.Executing,
            ResolvedBy = approval.ActorId,
            HumanApproval = approval,
            Version = proposal.Version + 1,
        };
        var changed = change switch
        {
            "scope" => proposal with { ScopeGoverned = true, RecognizedScopes = ["honua.mcp.full"] },
            "actor" => proposal with { ResolvedBy = "another-actor" },
            "plan" => proposal with { Plan = proposal.Plan with { Summary = "different plan" } },
            "instance" => proposal with { Audit = proposal.Audit with { OperationInstanceId = "another-instance" } },
            "initiation" => proposal with { Initiation = proposal.Initiation! with { Channel = OperationInitiationChannel.Mcp } },
            "missing-proof" => proposal with { HumanApproval = null },
            "channel" => proposal with { HumanApproval = approval with { Channel = "unknown" } },
            "pending" => proposal with { Status = OperationProposalStatus.AwaitingApproval },
            "version" => proposal with { Version = approval.ProposalVersion },
            _ => proposal,
        };
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(changed);
        var verifier = new OperationApprovalReplayVerifier(store);

        var accepted = await verifier.VerifyAsync(proposal.ProposalId, "approved-instance", proposal.SealedPlanHash!);

        accepted.Should().Be(expected);
    }

    [UnitTheory]
    [InlineData(OperationProposalStatus.AwaitingApproval, false)]
    [InlineData(OperationProposalStatus.Executing, true)]
    [InlineData(OperationProposalStatus.Submitted, false)]
    public async Task VerifyAsync_LegacyRecord_PreservesOnlyAnExistingExecutionClaim(
        OperationProposalStatus status, bool expected)
    {
        var proposal = OperationHumanApprovalTestHost.Proposal() with
        {
            Initiation = null,
            HumanApproval = null,
            Status = status,
            ResolvedBy = "recorded-legacy-approver",
            Audit = new OperationAuditInfo { OperationInstanceId = "legacy-instance" }
        };
        var store = Substitute.For<IOperationProposalStore>();
        store.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);

        var accepted = await new OperationApprovalReplayVerifier(store).VerifyAsync(
            proposal.ProposalId, "legacy-instance", proposal.SealedPlanHash!);

        accepted.Should().Be(expected);
        proposal.HumanApproval.Should().BeNull();
    }
}
