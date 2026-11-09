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
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class OperationGatewayApprovalConcurrencyTests
{
    // ── concurrent-claim tests ────────────────���───────────────────────────────

    [Fact]
    public async Task ApplyApprovedProposal_ConcurrentCalls_ExecutorCalledExactlyOnce()
    {
        // Arrange: two concurrent approval calls compete on the same AwaitingApproval proposal.
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(CreateProposal("p-concurrent", OperationProposalStatus.AwaitingApproval));
        var executor = new RecordingExecutor(() => Interlocked.Increment(ref executorCallCount));
        var sut = BuildGateway(store, executor);

        // Act: fire both tasks from thread-pool threads so the OS can interleave them. Each
        // call's outcome is captured independently (not into a shared success/failure variable
        // overwritten per iteration) so a real double-success or double-failure regression can
        // never be silently masked by one outcome overwriting the other.
        var t1 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", new OperationProposalApprovalContext { ApprovedBy = "admin-1", TenantId = string.Empty }));
        var t2 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", new OperationProposalApprovalContext { ApprovedBy = "admin-2", TenantId = string.Empty }));
        var outcomes = await Task.WhenAll(t1, t2);

        // Assert
        executorCallCount.Should().Be(1, "only the caller that wins the claim CAS may execute the operation");
        outcomes.Count(o => o.Success is not null).Should().Be(1, "exactly one approval call should succeed");
        outcomes.Count(o => o.Failure is not null).Should().Be(1, "exactly one approval call should fail");
        outcomes.Select(o => o.Failure).FirstOrDefault(f => f is not null)
            .Should().BeOfType<InvalidOperationException>("the losing caller should throw rather than double-execute");
    }

    private static async Task<(OperationProposal? Success, Exception? Failure)> CaptureOutcomeAsync(
        Func<Task<OperationProposal?>> action)
    {
        try
        {
            var result = await Task.Run(action).ConfigureAwait(false);
            return (result, null);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, ex);
        }
    }

    [Fact]
    public async Task ApplyApprovedProposal_WhenAlreadyClaimed_ThrowsWithoutExecuting()
    {
        // A proposal already in the Executing state (claimed by another concurrent call)
        // must be rejected at the initial status gate — not executed again.
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(CreateProposal("p-executing", OperationProposalStatus.Executing));
        var executor = new RecordingExecutor(() => Interlocked.Increment(ref executorCallCount));
        var sut = BuildGateway(store, executor);

        var act = () => sut.ApplyApprovedProposalAsync("p-executing", new OperationProposalApprovalContext { ApprovedBy = "admin", TenantId = string.Empty });

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Executing*cannot be approved*");
        executorCallCount.Should().Be(0, "a claimed proposal must not be executed again");
    }

    [Fact]
    public async Task ApplyApprovedProposal_WhenAlreadySubmitted_ThrowsWithoutExecuting()
    {
        // A proposal already resolved as Submitted (previous approval completed) must be
        // rejected — not executed a second time.
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(CreateProposal("p-submitted", OperationProposalStatus.Submitted));
        var executor = new RecordingExecutor(() => Interlocked.Increment(ref executorCallCount));
        var sut = BuildGateway(store, executor);

        var act = () => sut.ApplyApprovedProposalAsync("p-submitted", new OperationProposalApprovalContext { ApprovedBy = "admin", TenantId = string.Empty });

        await act.Should().ThrowAsync<InvalidOperationException>();
        executorCallCount.Should().Be(0, "an already-resolved proposal must not be executed again");
    }

    // ── sequential approval then reject confirms no double-execute ────────────

    [Fact]
    public async Task ApplyApprovedProposal_ThenRejectSameProposal_SecondCallThrows()
    {
        // After a successful approval (proposal → Executing → Submitted), a
        // subsequent RejectProposalAsync sees a non-AwaitingApproval status and throws.
        var store = new InMemoryProposalStore(CreateProposal("p-seq", OperationProposalStatus.AwaitingApproval));
        var sut = BuildGateway(store, new RecordingExecutor());

        // First call: approve
        var result = await sut.ApplyApprovedProposalAsync("p-seq", new OperationProposalApprovalContext { ApprovedBy = "admin", TenantId = string.Empty });
        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Submitted);

        // Second call: try to reject (should fail — no longer AwaitingApproval)
        var act = () => sut.RejectProposalAsync("p-seq", "admin", "too late");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── helpers ───────────��───────────────────────────────���──────────────────

    private static OperationProposal CreateProposal(string proposalId, OperationProposalStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new OperationProposalPlan { Summary = "test proposal" };
        return CanonicalOperationGatewayTestComposition.WithVersionedInitiation(new OperationProposal
        {
            ProposalId = proposalId,
            Kind = OperationClass.Deploy,
            Status = status,
            Plan = plan,
            SealedPlanHash = OperationApprovalPlanSeal.Compute(plan),
            Audit = new OperationAuditInfo
            {
                OperationInstanceId = $"opinst-{proposalId}",
                CorrelationId = $"corr-{proposalId}",
                Reason = "test",
            },
            CreatedAt = now,
            UpdatedAt = now,
        });
    }

    private static OperationGateway BuildGateway(
        IOperationProposalStore store,
        IOperationExecutor? executor = null)
    {
        var ladder = Substitute.For<IGuardrailLadder>();
        IEnumerable<IOperationExecutor> executors = executor != null
            ? [executor]
            : [];
        return CanonicalOperationGatewayTestComposition.Build(store, ladder, executors);
    }

    private sealed class InMemoryProposalStore(OperationProposal proposal) : IOperationProposalStore
    {
        private OperationProposal _proposal = proposal;
        private readonly Lock _lock = new();

        public Task<OperationProposal?> GetAsync(
            string proposalId,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _proposal.ProposalId == proposalId ? _proposal : (OperationProposal?)null);
            }
        }

        public Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_proposal.ProposalId != proposal.ProposalId || _proposal.Version != proposal.Version)
                {
                    return Task.FromResult(false);
                }

                _proposal = proposal with { Version = proposal.Version + 1 };
                return Task.FromResult(true);
            }
        }

        public Task<bool> TryCreateAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(false);

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
            OperationClass? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OperationProposal>>([_proposal]);

        public Task<bool> TryAcquireLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(
            string operationId,
            string ownerId,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(
            string operationId,
            string ownerId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingExecutor(Action? onExecute = null) : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(null);

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            onExecute?.Invoke();
            return Task.FromResult<string?>("exec-op-id");
        }
    }
}
