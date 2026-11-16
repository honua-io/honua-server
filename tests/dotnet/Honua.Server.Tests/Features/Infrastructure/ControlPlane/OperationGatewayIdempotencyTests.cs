// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.AuditLog;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Licensing.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationGatewayIdempotencyTests
{
    [Fact]
    public async Task RouteAsync_ApprovalTier_SameIdempotencyKey_ReturnsSingleProposal()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));

        var sut = BuildGateway(store, ladder);

        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "idem-123"
        };

        var first = await sut.RouteAsync(request);
        // Refresh-then-repropose: the caller re-issues the same request.
        var second = await sut.RouteAsync(request);

        first.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, first.Message);
        second.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        second.ProposalId.Should().Be(first.ProposalId, "the same idempotency key must fold onto the same proposal");
        store.Count.Should().Be(1, "re-proposing with the same idempotency key must not mint a duplicate proposal");
        store.ActiveCount.Should().Be(1);
        store.Single.Plan.Summary.Should().Be("Deploy proposal");
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task RouteAsync_ApprovalTier_ExpiredProposal_RequestsFreshApproval()
    {
        const OperationProposalStatus finished = OperationProposalStatus.Expired;
        var store = new MultiProposalStore();
        var sut = BuildGateway(store, ApprovalLadder());
        var request = new OperationGatewayRequest { Kind = OperationClass.Deploy, RequestedBy = "agent", IdempotencyKey = "finding-1" };

        var first = await sut.RouteAsync(request);
        store.SetStatus(first.ProposalId!, finished);
        var second = await sut.RouteAsync(request);
        var repeated = await sut.RouteAsync(request);
        store.SetStatus(second.ProposalId!, finished);
        var third = await sut.RouteAsync(request);

        second.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, second.Message);
        second.ProposalId.Should().NotBe(first.ProposalId);
        repeated.ProposalId.Should().Be(second.ProposalId, "an awaiting renewal folds like any active proposal");
        third.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, third.Message);
        third.ProposalId.Should().NotBe(first.ProposalId).And.NotBe(second.ProposalId);
        store.Count.Should().Be(3);
        store.ActiveCount.Should().Be(1);
        (await store.GetAsync(third.ProposalId!))!.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RouteAsync_ApprovalTier_RenewalDoesNotAliasLiteralCallerKey(bool literalFirst)
    {
        var store = new MultiProposalStore();
        var sut = BuildGateway(store, ApprovalLadder());
        var request = new OperationGatewayRequest { Kind = OperationClass.Deploy, RequestedBy = "agent", IdempotencyKey = "finding" };
        var literalRequest = request with { IdempotencyKey = "finding#1" };
        var first = await sut.RouteAsync(request);
        store.SetStatus(first.ProposalId!, OperationProposalStatus.Expired);

        var earlier = await sut.RouteAsync(literalFirst ? literalRequest : request);
        var later = await sut.RouteAsync(literalFirst ? request : literalRequest);

        earlier.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, earlier.Message);
        later.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, later.Message);
        later.ProposalId.Should().NotBe(earlier.ProposalId);
        (await sut.RouteAsync(literalRequest)).ProposalId.Should().Be(literalFirst ? earlier.ProposalId : later.ProposalId);
        (await sut.RouteAsync(request)).ProposalId.Should().Be(literalFirst ? later.ProposalId : earlier.ProposalId);
        store.Count.Should().Be(3);
        store.ActiveCount.Should().Be(2);
    }

    [Fact]
    [Trait("Tier", "Fast")]
    public async Task RouteAsync_ApprovalTier_ConcurrentRequestsAfterExpiry_CreateOneFreshProposal()
    {
        var store = new MultiProposalStore();
        var sut = BuildGateway(store, ApprovalLadder());
        var request = new OperationGatewayRequest { Kind = OperationClass.Deploy, RequestedBy = "agent", IdempotencyKey = "finding-3" };
        var first = await sut.RouteAsync(request);
        store.SetStatus(first.ProposalId!, OperationProposalStatus.Expired);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => sut.RouteAsync(request))));

        store.Count.Should().Be(2);
        store.ActiveCount.Should().Be(1);
        results.Where(result => result.ProposalId is not null)
            .Select(result => result.ProposalId)
            .Distinct()
            .Should().ContainSingle().Which.Should().NotBe(first.ProposalId);
    }

    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(OperationProposalStatus.Succeeded)]
    [InlineData(OperationProposalStatus.Rejected)]
    [InlineData(OperationProposalStatus.Failed)]
    [InlineData(OperationProposalStatus.Cancelled)]
    [InlineData(OperationProposalStatus.RolledBack)]
    public async Task RouteAsync_ApprovalTier_DecidedProposal_StaysSealed(OperationProposalStatus finished)
    {
        var store = new MultiProposalStore();
        var sut = BuildGateway(store, ApprovalLadder());
        var request = new OperationGatewayRequest { Kind = OperationClass.Deploy, RequestedBy = "agent", IdempotencyKey = "finding-2" };

        var first = await sut.RouteAsync(request);
        store.SetStatus(first.ProposalId!, finished);
        var second = await sut.RouteAsync(request);

        second.Outcome.Should().Be(OperationGatewayOutcome.Failed);
        second.ProposalId.Should().BeNull();
        store.Count.Should().Be(1, "a decided proposal is not reopened by repeating its idempotency key");
    }

    [Fact]
    public async Task RouteAsync_ApprovalTier_DifferentIdempotencyKeys_CreateDistinctProposals()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));

        var sut = BuildGateway(store, ladder);

        var first = await sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "idem-a"
        });
        var second = await sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "idem-b"
        });

        second.ProposalId.Should().NotBe(first.ProposalId);
        store.Count.Should().Be(2);
    }

    [Fact]
    public async Task CreateApprovalProposalAsync_MissingPlan_UsesCanonicalPlanner()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        var sut = BuildGateway(store, ladder);

        var result = await sut.CreateApprovalProposalAsync(
            "opinst-forced-proposal",
            new OperationGatewayRequest
            {
                Kind = OperationClass.Deploy,
                RequestedBy = "agent",
            });

        result.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated, result.Message);
        store.Single.Plan.Summary.Should().Be("Deploy proposal");
    }

    [Fact]
    public async Task RouteAsync_CanceledDuringAudit_FinalizesPlannedProposal()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        var sut = BuildGateway(store, ladder, new CancelingOnceAuditLog());

        var route = () => sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "cancel-during-audit"
        });

        await route.Should().ThrowAsync<OperationCanceledException>();
        store.Single.Status.Should().Be(OperationProposalStatus.Failed);
        store.Single.ResolutionReason.Should().Be("Durable audit acceptance failed.");
        store.ActiveCount.Should().Be(0, "a canceled request must not orphan a Planned proposal");

        var retry = await sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "cancel-during-audit"
        });
        retry.Outcome.Should().Be(OperationGatewayOutcome.Failed,
            "a terminal failed proposal must never be returned as actionable approval work");
        retry.ProposalId.Should().BeNull();
    }

    [Fact]
    public async Task RouteAsync_AuditInfrastructureThrows_FinalizesAndAuditsPlannedProposalFailure()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        var audit = new ThrowingOnceAuditLog();
        var sut = BuildGateway(store, ladder, audit);

        var result = await sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            IdempotencyKey = "fault-during-audit",
        });

        result.Outcome.Should().Be(OperationGatewayOutcome.Failed);
        result.ProposalId.Should().BeNull();
        store.Single.Status.Should().Be(OperationProposalStatus.Failed);
        store.ActiveCount.Should().Be(0);
        audit.Events.Should().Contain(item =>
            item.Action == "operation.proposal_failed" && item.Outcome == AuditOutcome.Failure);
    }

    [Fact]
    public async Task ApplyApprovedProposalAsync_ConsumesPersistedValidatedPlanWithoutReplanning()
    {
        var store = new MultiProposalStore();
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        var actuator = new PlanCapturingExecutor();
        var sut = CanonicalOperationGatewayTestComposition.Build(store, ladder, [actuator]);

        var routed = await sut.RouteAsync(new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "agent",
            ExecutionPayload = "live-request-payload",
        });
        var persisted = store.Single;

        await sut.ApplyApprovedProposalAsync(routed.ProposalId!, new OperationProposalApprovalContext { ApprovedBy = "approver", TenantId = string.Empty });

        persisted.Plan.Summary.Should().Be("validated deploy plan");
        persisted.Plan.ExecutionPayload.Should().Be("validated-plan-payload");
        actuator.PlanCalls.Should().Be(1, "approved replay must not revalidate against live state");
        actuator.ExecutedPayload.Should().Be("validated-plan-payload");
    }

    private static IGuardrailLadder ApprovalLadder()
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.RequiresApproval, OperationClass.Deploy, HonuaEdition.Pro, "test"));
        return ladder;
    }

    private static OperationGateway BuildGateway(
        IOperationProposalStore store,
        IGuardrailLadder ladder,
        IAuditLog? auditLog = null)
        => CanonicalOperationGatewayTestComposition.Build(
            store,
            ladder,
            [CanonicalOperationGatewayTestComposition.PlanningOnly(OperationClass.Deploy)],
            auditLog is null ? null : services => services.AddSingleton(auditLog));

    private sealed class CancelingOnceAuditLog : IAuditLog
    {
        private int _calls;

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromException<string?>(new OperationCanceledException(cancellationToken));
            }

            return Task.FromResult<string?>("audit-retry");
        }
    }

    private sealed class ThrowingOnceAuditLog : IAuditLog
    {
        private int _calls;

        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromException<string?>(new InvalidOperationException("audit unavailable"));
            }

            Events.Add(auditEvent);
            return Task.FromResult<string?>("audit-failure");
        }
    }

    private sealed class PlanCapturingExecutor
        : Honua.Core.Features.ControlPlane.Abstractions.IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;

        public int PlanCalls { get; private set; }

        public string? ExecutedPayload { get; private set; }

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            PlanCalls++;
            return Task.FromResult<OperationProposalPlan?>(new OperationProposalPlan
            {
                Summary = "validated deploy plan",
                Diff = ["old -> new"],
                RiskLevel = ProposalRiskLevel.High,
                Warnings = ["review canary"],
                ExecutionPayload = "validated-plan-payload",
            });
        }

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            ExecutedPayload = executionPayload;
            return Task.FromResult<string?>("deploy-job-1");
        }
    }

    private sealed class MultiProposalStore : IOperationProposalStore
    {
        private readonly Dictionary<string, OperationProposal> _proposals = new(StringComparer.Ordinal);
        private readonly Lock _lock = new();

        public int Count
        {
            get { lock (_lock) { return _proposals.Count; } }
        }

        public int ActiveCount
        {
            get { lock (_lock) { return _proposals.Values.Count(IsActive); } }
        }

        public OperationProposal Single
        {
            get { lock (_lock) { return _proposals.Values.Single(); } }
        }

        public void SetStatus(string proposalId, OperationProposalStatus status)
        {
            lock (_lock)
            {
                _proposals[proposalId] = _proposals[proposalId] with { Status = status };
            }
        }

        public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(_proposals.TryGetValue(proposalId, out var proposal) ? proposal : null);
            }
        }

        public Task<bool> TryCreateAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_proposals.ContainsKey(proposal.ProposalId))
                {
                    return Task.FromResult(false);
                }

                _proposals[proposal.ProposalId] = proposal with { Version = 1 };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (!_proposals.TryGetValue(proposal.ProposalId, out var current) || current.Version != proposal.Version)
                {
                    return Task.FromResult(false);
                }

                _proposals[proposal.ProposalId] = proposal with { Version = proposal.Version + 1 };
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
            OperationClass? kind = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                var active = _proposals.Values
                    .Where(IsActive)
                    .Where(proposal => kind == null || proposal.Kind == kind)
                    .ToList();
                return Task.FromResult<IReadOnlyList<OperationProposal>>(active);
            }
        }

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static bool IsActive(OperationProposal proposal)
            => proposal.Status is not (OperationProposalStatus.Succeeded
                or OperationProposalStatus.Failed
                or OperationProposalStatus.Rejected
                or OperationProposalStatus.RolledBack
                or OperationProposalStatus.Cancelled
                or OperationProposalStatus.Expired);
    }
}
