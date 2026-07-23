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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Regression coverage for BH4-031: concurrent calls to
/// <see cref="OperationGateway.ApplyApprovedProposalAsync"/> must execute the
/// underlying operation exactly once, even when two callers both read
/// <see cref="OperationProposalStatus.AwaitingApproval"/> before either claims.
///
/// The fix transitions the proposal from AwaitingApproval → Executing via an
/// atomic CAS write (single-flight guard) before invoking the executor.  The
/// caller that loses the CAS re-reads a non-AwaitingApproval status and throws
/// rather than proceeding to execute.
/// </summary>
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
        var t1 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", "admin-1"));
        var t2 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", "admin-2"));
        var outcomes = await Task.WhenAll(t1, t2);

        // Assert
        executorCallCount.Should().Be(1, "only the caller that wins the claim CAS may execute the operation");
        outcomes.Count(o => o.Success is not null).Should().Be(1, "exactly one approval call should succeed");
        outcomes.Count(o => o.Failure is not null).Should().Be(1, "exactly one approval call should fail");
        outcomes.Select(o => o.Failure).FirstOrDefault(f => f is not null)
            .Should().BeOfType<InvalidOperationException>("the losing caller should throw rather than double-execute");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a thread-pool thread and captures its outcome as a
    /// (Success, Failure) pair instead of letting an exception propagate. The broad catch here
    /// is intentional: it captures whatever the concurrent call actually threw so the caller can
    /// assert on its type, rather than swallowing a signal that would otherwise fail the test.
    /// </summary>
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

        var act = () => sut.ApplyApprovedProposalAsync("p-executing", "admin");

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

        var act = () => sut.ApplyApprovedProposalAsync("p-submitted", "admin");

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
        var result = await sut.ApplyApprovedProposalAsync("p-seq", "admin");
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
        return new OperationProposal
        {
            ProposalId = proposalId,
            Kind = OperationClass.Deploy,
            Status = status,
            Plan = new OperationProposalPlan { Summary = "test proposal" },
            Audit = new OperationAuditInfo { Reason = "test" },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static OperationGateway BuildGateway(
        IOperationProposalStore store,
        IOperationExecutor? executor = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IAuditLog>(_ => NullAuditLog.Instance);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var ladder = Substitute.For<IGuardrailLadder>();
        var notifier = Substitute.For<IProposalNotifier>();
        IEnumerable<IOperationExecutor> executors = executor != null
            ? [executor]
            : [];

        return new OperationGateway(
            ladder,
            store,
            executors,
            scopeFactory,
            notifier,
            NullLogger<OperationGateway>.Instance);
    }

    /// <summary>
    /// Thread-safe in-memory proposal store with CAS semantics (matching Redis behaviour).
    /// </summary>
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

    /// <summary>
    /// Executor that records how many times it was called and invokes an optional callback.
    /// </summary>
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
