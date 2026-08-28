// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Regression coverage for the propose idempotency gap (#1693): re-proposing an
/// approval-tier operation with the same idempotency key previously minted a second
/// AwaitingApproval proposal instead of folding onto the existing one.
/// </summary>
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

        first.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        second.Outcome.Should().Be(OperationGatewayOutcome.ProposalCreated);
        second.ProposalId.Should().Be(first.ProposalId, "the same idempotency key must fold onto the same proposal");
        store.Count.Should().Be(1, "re-proposing with the same idempotency key must not mint a duplicate proposal");
        store.ActiveCount.Should().Be(1);
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
                or OperationProposalStatus.RolledBack);
    }
}
