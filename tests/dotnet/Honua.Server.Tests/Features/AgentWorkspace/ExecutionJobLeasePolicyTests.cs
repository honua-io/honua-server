// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Import.FileImport.Durable;
using Honua.Core.Features.Security.Domain;
using Honua.Server.Features.Operations;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.AgentWorkspace;

// Real plan sealing and receipt verification; canonical identity/resource and
// operator policy ports are controlled. Not a full approval-inbox HTTP journey.
public sealed class ExecutionJobLeasePolicyTests
{
    [Fact]
    public async Task BenignJobNeedsNoProposalButCurrentPolicyCanRequireOneLater()
    {
        var fixture = new Fixture();
        var job = fixture.Job with { Audit = fixture.Job.Audit with { ProposalId = null } };
        var decision = await fixture.Policy.AuthorizeIssuanceAsync(fixture.Request(job), fixture.Scope);
        Assert.True(decision.IsAllowed);
        Assert.Null(decision.ApprovalReceiptId);
        Assert.Empty(fixture.Store.ReceivedCalls());
        await fixture.Policy.RequireCurrentAsync(job, decision, fixture.Scope, fixture.Current);
        fixture.Requirement = ApprovalRequirement.Required("new-policy", "new-reason");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.RequireCurrentAsync(job, decision, fixture.Scope, fixture.Current));
    }

    [Fact]
    public async Task ExactDistinctHumanApprovalAllowsOriginalJobAndCapsItsLease()
    {
        var fixture = new Fixture();
        await fixture.ApproveAsync();
        var decision = await fixture.Policy.AuthorizeIssuanceAsync(fixture.Request(fixture.Job), fixture.Scope);
        Assert.Equal(fixture.Proposal!.ProposalId, decision.ApprovalReceiptId);
        Assert.Equal(fixture.Proposal.SealedPlanHash, decision.DecisionId);
        Assert.Contains("roads.geojson", fixture.Proposal.Plan.Summary);
        Assert.Contains(fixture.Proposal.Plan.Diff, line => line.Contains("honua_data.roads", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.Proposal.Plan.Diff, line => line.Contains("private-source", StringComparison.Ordinal));
        Assert.Equal(fixture.Now.AddMinutes(30), decision.MaximumExpiresAt);
        await fixture.Policy.RequireCurrentAsync(fixture.Job, decision, fixture.Scope, fixture.Current);
        // A policy becoming less restrictive does not erase the original receipt.
        fixture.Requirement = ApprovalRequirement.NotRequired();
        await fixture.Policy.RequireCurrentAsync(fixture.Job, decision, fixture.Scope, fixture.Current);
        fixture.Proposal = fixture.Proposal with { Status = OperationProposalStatus.Cancelled };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.RequireCurrentAsync(fixture.Job, decision, fixture.Scope, fixture.Current));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unclaimed")]
    [InlineData("same-human")]
    [InlineData("agent-approver")]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("instance")]
    [InlineData("different-job")]
    [InlineData("audit-missing")]
    [InlineData("plan-edited")]
    [InlineData("descriptor")]
    [InlineData("class")]
    [InlineData("future-claim")]
    [InlineData("predated-claim")]
    [InlineData("expired")]
    [InlineData("rejected")]
    public async Task ApprovalSubstitutionOrRevocationCannotIssueLease(string change)
    {
        var fixture = new Fixture();
        await fixture.ApproveAsync();
        var original = fixture.Proposal!;
        fixture.Proposal = change switch
        {
            "missing" => null,
            "unclaimed" => original with { Status = OperationProposalStatus.AwaitingApproval, ApprovedAtClaim = null },
            "same-human" => original with { ApprovedByAtClaim = original.RequestedBy!.ToUpperInvariant() },
            "agent-approver" => original with { RequestedByAgent = "agent", ApprovedByAtClaim = "agent" },
            "tenant" => original with { TenantId = "other" },
            "owner" => original with { RequestedBy = "other" },
            "instance" => original with { Audit = original.Audit with { OperationInstanceId = "other" } },
            "different-job" => original with { ExecutionOperationId = "other" },
            "audit-missing" => original with { Audit = original.Audit with { AuditId = null } },
            "plan-edited" => original with { Plan = original.Plan with { Summary = "changed after review" } },
            "descriptor" => original with { OperationId = "other" },
            "class" => original with { Kind = OperationClass.Deploy },
            "future-claim" => original with { ApprovedAtClaim = fixture.Now.AddMinutes(1) },
            "predated-claim" => original with { ApprovedAtClaim = original.CreatedAt.AddSeconds(-1) },
            "rejected" => original with { Status = OperationProposalStatus.Rejected },
            _ => original
        };
        if (change == "expired") fixture.Clock.UtcNow = fixture.Now.AddMinutes(30);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.AuthorizeIssuanceAsync(fixture.Request(fixture.Job), fixture.Scope));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("resource")]
    [InlineData("side-effect")]
    [InlineData("approval-policy")]
    [InlineData("approval-reasons")]
    [InlineData("authorization")]
    [InlineData("receipt-id")]
    [InlineData("decision-id")]
    [InlineData("deadline")]
    public async Task AcceptedJobRechecksExactIntentScopePolicyAndRetainedDecision(string change)
    {
        var fixture = new Fixture();
        await fixture.ApproveAsync();
        var decision = await fixture.Policy.AuthorizeIssuanceAsync(fixture.Request(fixture.Job), fixture.Scope);
        var job = fixture.Job;
        var scope = fixture.Scope;
        switch (change)
        {
            case "intent": job = job with { Spec = job.Spec with { WorkloadName = "other" } }; break;
            case "resource": scope = scope with { Resources = [new() { ResourceType = OperatorResourceType.Catalog, ResourceId = "other", Operation = AuthorizationOperation.Read }] }; break;
            case "side-effect": scope = scope with { Operations = [scope.Operations[0] with { IsDestructive = true }] }; break;
            case "approval-policy": fixture.Requirement = ApprovalRequirement.Required("other", "replace"); break;
            case "approval-reasons": fixture.Requirement = ApprovalRequirement.Required("import-review", "other"); break;
            case "authorization": fixture.Allowed = false; break;
            case "receipt-id": decision = decision with { ApprovalReceiptId = "other" }; break;
            case "decision-id": decision = decision with { DecisionId = "other" }; break;
            case "deadline": decision = decision with { MaximumExpiresAt = fixture.Now.AddHours(2) }; break;
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.RequireCurrentAsync(job, decision, scope, fixture.Current));
    }

    [Fact]
    public async Task OAuthScopeDenialPrecedesOrdinaryAuthorizationAndApprovalPlanning()
    {
        var fixture = new Fixture();
        fixture.Scopes.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>())
            .Returns(OperatorScopeDecision.Denied("read-only"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Policy.AuthorizeIssuanceAsync(fixture.Request(fixture.Job), fixture.Scope));
        Assert.Empty(fixture.Authorization.ReceivedCalls());
        Assert.Empty(fixture.Approvals.ReceivedCalls());
    }

    private sealed class Fixture
    {
        internal DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;
        internal TestClock Clock { get; }
        internal IOperationProposalStore Store { get; } = Substitute.For<IOperationProposalStore>();
        internal IOperatorScopeAuthorizer Scopes { get; } = Substitute.For<IOperatorScopeAuthorizer>();
        internal IOperatorAuthorizationEvaluator Authorization { get; } = Substitute.For<IOperatorAuthorizationEvaluator>();
        internal IOperatorApprovalEvaluator Approvals { get; } = Substitute.For<IOperatorApprovalEvaluator>();
        internal IJobExecutionLeaseCurrentPolicy Current { get; } = Substitute.For<IJobExecutionLeaseCurrentPolicy>();
        internal ApprovalRequirement Requirement { get; set; } = ApprovalRequirement.NotRequired();
        internal bool Allowed { get; set; } = true;
        internal OperationProposal? Proposal { get; set; }
        internal ExecutionJobLeasePolicy Policy { get; }
        internal ExecutionJobRecord Job { get; }
        internal JobExecutionLeaseScope Scope { get; } = new()
        {
            Kind = ExecutionJobKind.FileImport, ProviderBinding = "original-destination-and-source", Resources = [],
            Operations = [new() { ResourceType = OperatorResourceType.Catalog, ResourceId = "destination", Operation = OperatorOperation.Create }]
        };
        internal ClaimsPrincipal Executor { get; } = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "executor")], "test"));

        internal Fixture()
        {
            Clock = new(Now);
            Job = new()
            {
                OperationId = "job-original", Status = ExecutionJobStatus.Queued, CreatedAt = Now, UpdatedAt = Now,
                Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", WorkloadName = "import",
                    Parameters = FileImportJobParameters.Build(new ImportRequest { CloudFileId = "private-source", FileName = "roads.geojson",
                        TableName = "roads", TargetSchema = "honua_data" }, 4096) },
                Audit = new() { OperationInstanceId = "job-original", ProposalId = Honua.ControlPlane.OperationGateway.DeriveProposalId(OperationClass.AdminConfigChange, "dataset-import.submit", "original-intent", "tenant"), RequestedBy = "executor", TenantId = "tenant" }
            };
            Store.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Proposal));
            Scopes.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorResourceType>(), Arg.Any<OperatorOperation>()).Returns(OperatorScopeDecision.NotGoverned());
            Authorization.EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>()).Returns(_ => Allowed ? AccessDecision.Allowed() : AccessDecision.Forbidden("revoked"));
            Approvals.Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>()).Returns(_ => Requirement);
            Current.EvaluateAsync(Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>()).Returns(_ => new JobExecutionLeaseCurrentPolicyDecision
            { Authorization = Allowed ? AccessDecision.Allowed() : AccessDecision.Forbidden("revoked"), Approval = Requirement });
            Policy = new(Store, Scopes, Authorization, Approvals, Clock);
        }

        internal JobExecutionLeaseIssuanceRequest Request(ExecutionJobRecord job) => new()
        { PreparedJob = job, Executor = Executor, RequestedExpiresAt = Now.AddHours(1) };

        internal async Task ApproveAsync()
        {
            Requirement = ApprovalRequirement.Required("import-review", "replace");
            var gateway = new OperationGatewayRequest
            {
                OperationId = "dataset-import.submit", OperationInstanceId = Job.OperationId, Kind = OperationClass.AdminConfigChange,
                RequestedBy = Job.Audit.RequestedBy, TenantId = Job.Audit.TenantId, IdempotencyKey = "original-intent"
            };
            var plan = await Policy.PrepareApprovalPlanAsync(Job, Scope, Executor, gateway, Now.AddMinutes(30));
            Proposal = new()
            {
                ProposalId = Job.Audit.ProposalId!, OperationId = "dataset-import.submit", Kind = OperationClass.AdminConfigChange,
                TenantId = "tenant", RequestedBy = "executor", Status = OperationProposalStatus.Executing,
                Plan = plan, SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
                Audit = new() { OperationInstanceId = Job.OperationId, AuditId = "audit-original", ApprovalPolicyRef = "import-review" },
                ApprovedByAtClaim = "human-approver", ApprovedAtClaim = Now, CreatedAt = Now, UpdatedAt = Now
            };
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        internal DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
