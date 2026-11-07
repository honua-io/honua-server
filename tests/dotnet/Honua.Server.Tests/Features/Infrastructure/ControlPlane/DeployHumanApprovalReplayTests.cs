// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.ControlPlane.Executors;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.ControlPlane;

[SecurityTest]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.ApprovalManagement)]
public sealed class DeployHumanApprovalReplayTests
{
    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnqueueAsync_VerifiedHumanApproval_AllowsRequesterOrAnotherHuman(bool requesterApproves)
    {
        using var host = new OperationHumanApprovalTestHost();
        var proposal = await ApprovedAsync(host, requesterApproves);
        var store = Substitute.For<IWorkflowOperationStore>();
        store.TryCreateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>()).Returns(true);
        var proposals = Substitute.For<IOperationProposalStore>();
        proposals.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);

        var id = await new DurableRestoreQueue(store, proposals).EnqueueAsync(
            Request(proposal), proposal.Plan.ExecutionPayload!, CancellationToken.None);

        Assert.Equal(proposal.ExecutionOperationId, id);
        await store.Received(1).TryCreateAsync(Arg.Is<WorkflowOperationRecord>(record =>
            record.Audit.ProposalId == proposal.ProposalId && record.Audit.TenantId == proposal.TenantId &&
            record.Audit.OperationInstanceId == proposal.Audit.OperationInstanceId &&
            record.PreMigrationRestore!.ApprovedBy == proposal.ResolvedBy), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("proof")]
    [InlineData("binding")]
    [InlineData("seal")]
    [InlineData("pending")]
    [InlineData("instance")]
    [InlineData("tenant")]
    [InlineData("actor")]
    [InlineData("payload")]
    public async Task EnqueueAsync_ChangedApproval_RefusesBeforeWriting(string change)
    {
        using var host = new OperationHumanApprovalTestHost();
        var original = await ApprovedAsync(host, requesterApproves: false);
        var proposal = change switch
        {
            "proof" => original with { HumanApproval = null },
            "binding" => original with { HumanApproval = original.HumanApproval! with { BindingDigest = new string('0', 64) } },
            "seal" => original with { SealedPlanHash = new string('0', 64) },
            "pending" => original with { Status = OperationProposalStatus.AwaitingApproval },
            "instance" => original with { Audit = original.Audit with { OperationInstanceId = "different-instance" } },
            "tenant" => original with { TenantId = "another-tenant" },
            "actor" => original with { ResolvedBy = "another-human" },
            "payload" => original with { Plan = original.Plan with { ExecutionPayload = "{}" } },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var store = Substitute.For<IWorkflowOperationStore>();
        var proposals = Substitute.For<IOperationProposalStore>();
        proposals.GetAsync(proposal.ProposalId, Arg.Any<CancellationToken>()).Returns(proposal);
        var queue = new DurableRestoreQueue(store, proposals);

        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.EnqueueAsync(
            Request(original), original.Plan.ExecutionPayload!, CancellationToken.None));

        await store.DidNotReceive().TryCreateAsync(Arg.Any<WorkflowOperationRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    private static OperationGatewayRequest Request(OperationProposal proposal) => new()
    {
        Kind = OperationClass.Deploy,
        RequestedBy = proposal.RequestedBy,
        ApprovedBy = proposal.ResolvedBy,
        ApprovedProposalId = proposal.ProposalId,
        OperationInstanceId = proposal.Audit.OperationInstanceId,
        TenantId = proposal.TenantId,
    };

    private static async Task<OperationProposal> ApprovedAsync(OperationHumanApprovalTestHost host, bool requesterApproves)
    {
        var human = await host.CreateHumanAsync();
        var actor = CanonicalSecurityActor.Resolve(human.Principal)!.ActorId;
        var proposal = OperationHumanApprovalTestHost.Proposal(requesterApproves ? actor : "agent:requester");
        var plan = new OperationProposalPlan
        {
            Summary = "Restore a reviewed revision",
            ExecutionPayload = new DeployExecutionPayload { TargetId = "cell", DesiredRevision = "prior", Rollback = true }.Serialize(),
        };
        var planHash = OperationApprovalPlanSeal.Compute(plan);
        proposal = proposal with
        {
            Kind = OperationClass.Deploy,
            OperationId = LegacyOperationIds.For(OperationClass.Deploy),
            Plan = plan,
            SealedPlanHash = planHash,
            Audit = proposal.Audit with { OperationInstanceId = "approved-instance" },
            Initiation = proposal.Initiation! with { PlanHash = planHash },
        };
        host.ReviewRequest(human);
        var review = await host.Authority.OpenReviewAsync(proposal, host.Context);
        host.ApproveRequest(human, review);
        var approval = await host.Authority.VerifyAsync(proposal, OperationHumanApprovalTestHost.Approval(human));
        return proposal with
        {
            Status = OperationProposalStatus.Executing,
            ResolvedBy = approval.ActorId,
            HumanApproval = approval,
            ExecutionOperationId = "restore-approved-claim",
            Version = proposal.Version + 1,
        };
    }
}
