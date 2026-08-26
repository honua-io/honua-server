// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
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
/// <see cref="OperationGateway.ApplyApprovedProposalAsync(string, OperationApproverIdentity, CancellationToken)"/>
/// must execute the
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
        var executor = new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount));
        var sut = BuildGateway(store, executor);

        // Act: fire both tasks from thread-pool threads so the OS can interleave them. Each
        // call's outcome is captured independently (not into a shared success/failure variable
        // overwritten per iteration) so a real double-success or double-failure regression can
        // never be silently masked by one outcome overwriting the other.
        var t1 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", Approver("admin-1")));
        var t2 = CaptureOutcomeAsync(() => sut.ApplyApprovedProposalAsync("p-concurrent", Approver("admin-2")));
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
        var executor = new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount));
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
        var executor = new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount));
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
        var result = await sut.ApplyApprovedProposalAsync("p-seq", Approver("admin"));
        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Submitted);

        // Second call: try to reject (should fail — no longer AwaitingApproval)
        var act = () => sut.RejectProposalAsync("p-seq", "admin", "too late");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ApplyApprovedProposal_ConcurrentRejection_CannotOverwriteExecutionClaim()
    {
        var bothInitialReadsCompleted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInitialReads = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionClaimPersisted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var initialReadCount = 0;

        async Task BeforeGetAsync()
        {
            var read = Interlocked.Increment(ref initialReadCount);
            if (read > 2)
            {
                return;
            }

            if (read == 2)
            {
                bothInitialReadsCompleted.TrySetResult(true);
            }

            await releaseInitialReads.Task;
        }

        async Task BeforeTrySetAsync(OperationProposal candidate)
        {
            if (candidate.Status == OperationProposalStatus.Rejected)
            {
                await executionClaimPersisted.Task;
            }
        }

        void AfterTrySet(OperationProposal persisted)
        {
            if (persisted.Status == OperationProposalStatus.Executing)
            {
                executionClaimPersisted.TrySetResult(true);
            }
        }

        var store = new InMemoryProposalStore(
            CreateProposal("p-approve-reject-race", OperationProposalStatus.AwaitingApproval),
            BeforeGetAsync,
            BeforeTrySetAsync,
            AfterTrySet);
        var executor = new BlockingExecutor();
        var sut = BuildGateway(store, executor);

        var approvalTask = sut.ApplyApprovedProposalAsync("p-approve-reject-race", Approver("approver"));
        var rejectionTask = sut.RejectProposalAsync("p-approve-reject-race", "rejector", "deny");

        await bothInitialReadsCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseInitialReads.TrySetResult(true);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            var reject = async () => await rejectionTask;
            await reject.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*transitioned from*AwaitingApproval*Executing*concurrent decision*");
            store.Snapshot.Status.Should().Be(OperationProposalStatus.Executing);
            store.Snapshot.Approval.Should().Match<OperationApprovalRecord>(approval =>
                approval.Approved && approval.ProposerAuthorityRetained);
        }
        finally
        {
            executor.Release.TrySetResult(true);
        }

        var approved = await approvalTask;

        approved.Should().NotBeNull();
        approved!.Status.Should().Be(OperationProposalStatus.Submitted);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.Submitted);
        store.Snapshot.Approval.Should().Match<OperationApprovalRecord>(approval =>
            approval.Approved && approval.ProposerAuthorityRetained);
    }

    [Fact]
    public async Task ApplyApprovedProposal_PersistsApprovalAndAuthorityBeforeActuation()
    {
        var authority = ValidAuthority();
        var store = new InMemoryProposalStore(
            CreateProposal("p-evidence-first", OperationProposalStatus.AwaitingApproval, authority));
        OperationProposal? observedProposal = null;
        OperationGatewayRequest? observedRequest = null;
        var executor = new RecordingExecutor(request =>
        {
            observedProposal = store.Snapshot;
            observedRequest = request;
        });
        var sut = BuildGateway(store, executor);

        var approver = Approver("separate-approver");
        var result = await sut.ApplyApprovedProposalAsync("p-evidence-first", approver);

        observedProposal.Should().NotBeNull();
        observedProposal!.Status.Should().Be(OperationProposalStatus.Executing);
        observedProposal.ResolvedBy.Should().Be("separate-approver");
        observedProposal.Approval.Should().NotBeNull();
        observedProposal.Approval!.Approved.Should().BeTrue();
        observedProposal.Approval.Approver.Should().Be("separate-approver");
        observedProposal.Approval.ApproverIdentity.Should().BeEquivalentTo(approver);
        observedProposal.Approval.ProposerAuthorityRetained.Should().BeTrue();
        observedProposal.Authority.Should().BeEquivalentTo(authority);
        observedRequest.Should().NotBeNull();
        observedRequest!.Authority.Should().BeEquivalentTo(authority);
        result!.Approval.Should().BeEquivalentTo(observedProposal.Approval);
    }

    [Fact]
    public async Task ApplyApprovedProposal_LegacyRecordWithoutCapturedAuthority_FailsClosed()
    {
        var store = new InMemoryProposalStore(
            CreateProposal(
                "p-legacy-authority",
                OperationProposalStatus.AwaitingApproval,
                omitAuthority: true));
        var executorCallCount = 0;
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync("p-legacy-authority", "separate-approver");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*legacy record without captured proposer authority*cannot be executed*Resubmit*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        store.Snapshot.Approval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_LegacyStringIdentityWithCapturedAuthority_FailsClosed()
    {
        var store = new InMemoryProposalStore(
            CreateProposal("p-raw-approver", OperationProposalStatus.AwaitingApproval));
        var executorCallCount = 0;
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync("p-raw-approver", "separate-approver");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*issuer-qualified approver identity is required*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        store.Snapshot.Approval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_QualifiedIdentityAgainstLegacyGateway_FailsClosed()
    {
        IOperationGateway gateway = new LegacyStringOnlyGateway();

        var act = () => gateway.ApplyApprovedProposalAsync("proposal-1", Approver("approver"));

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*issuer-qualified proposal approval*");
        ((LegacyStringOnlyGateway)gateway).StringApprovalCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyApprovedProposal_WithInvalidPersistedAuthority_FailsBeforeActuation()
    {
        var invalidAuthority = ValidAuthority() with { ScopeCeiling = ["service:admin"] };
        var store = new InMemoryProposalStore(
            CreateProposal("p-invalid-authority", OperationProposalStatus.AwaitingApproval, invalidAuthority));
        var executorCallCount = 0;
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync("p-invalid-authority", Approver("separate-approver"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authority is invalid*scope ceiling*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        store.Snapshot.Approval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_SameApiKeyCannotApproveItsOwnProposal()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, "admin"),
                new Claim("api_key_id", "api-key-42"),
                new Claim("api_key_name", "release automation"),
            ],
            "ApiKey"));
        var authority = OperationAuthorityContext.Capture(principal, "tenant-1");
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(
            CreateProposal("p-api-key-self-approval", OperationProposalStatus.AwaitingApproval, authority));
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync(
            "p-api-key-self-approval",
            OperationApproverIdentity.Capture(principal));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposer cannot approve its own operation*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
    }

    [Fact]
    public async Task ApplyApprovedProposal_SameOperatorSubjectFromDifferentMembershipIssuer_CanApprove()
    {
        var proposer = CreateOperatorPrincipal("shared-subject", "https://idp-a.example");
        var approver = CreateOperatorPrincipal("shared-subject", "https://idp-b.example");
        var authority = OperationAuthorityContext.Capture(proposer, "tenant-1");
        var approverIdentity = OperationApproverIdentity.Capture(approver);
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(
            CreateProposal("p-multi-provider-approval", OperationProposalStatus.AwaitingApproval, authority));
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var result = await sut.ApplyApprovedProposalAsync(
            "p-multi-provider-approval",
            approverIdentity);

        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Submitted);
        result.ResolvedBy.Should().Be("shared-subject");
        result.Approval!.Approver.Should().Be("shared-subject");
        result.Approval.ApproverIdentity.Should().BeEquivalentTo(approverIdentity);
        executorCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ApplyApprovedProposal_SameIssuerQualifiedOperatorIdentity_RemainsForbidden()
    {
        var principal = CreateOperatorPrincipal("shared-subject", "https://idp-a.example");
        var authority = OperationAuthorityContext.Capture(principal, "tenant-1");
        var approverIdentity = OperationApproverIdentity.Capture(principal);
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(
            CreateProposal("p-qualified-self-approval", OperationProposalStatus.AwaitingApproval, authority));
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync(
            "p-qualified-self-approval",
            approverIdentity);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposer cannot approve its own operation*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Status.Should().Be(OperationProposalStatus.AwaitingApproval);
        store.Snapshot.Approval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_SameUpstreamIdentityAcrossBearerTransports_RemainsForbidden()
    {
        var proposer = CreateOperatorPrincipal(
            "shared-subject",
            membershipIssuer: null,
            scheme: "Bearer",
            issuer: "https://idp-a.example");
        var approver = CreateOperatorPrincipal("shared-subject", "https://idp-a.example");
        var authority = OperationAuthorityContext.Capture(proposer, "tenant-1");
        var approverIdentity = OperationApproverIdentity.Capture(approver);
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(
            CreateProposal("p-cross-transport-self-approval", OperationProposalStatus.AwaitingApproval, authority));
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync(
            "p-cross-transport-self-approval",
            approverIdentity);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposer cannot approve its own operation*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Approval.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_LegacyOperatorIssuerProvenance_RejectsSameActorConservatively()
    {
        var proposer = CreateOperatorPrincipal("shared-subject", membershipIssuer: null);
        var approver = CreateOperatorPrincipal("shared-subject", "https://idp-b.example");
        var authority = OperationAuthorityContext.Capture(proposer, "tenant-1");
        var approverIdentity = OperationApproverIdentity.Capture(approver);
        var executorCallCount = 0;
        var store = new InMemoryProposalStore(
            CreateProposal("p-legacy-membership-issuer", OperationProposalStatus.AwaitingApproval, authority));
        var sut = BuildGateway(
            store,
            new RecordingExecutor(_ => Interlocked.Increment(ref executorCallCount)));

        var act = () => sut.ApplyApprovedProposalAsync(
            "p-legacy-membership-issuer",
            approverIdentity);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposer cannot approve its own operation*");
        executorCallCount.Should().Be(0);
        store.Snapshot.Approval.Should().BeNull();
    }

    // Helpers

    private static OperationApproverIdentity Approver(string actor) => new()
    {
        Actor = actor,
        Issuer = "https://approver.example",
        Scheme = "Bearer",
    };

    private static ClaimsPrincipal CreateOperatorPrincipal(
        string subject,
        string? membershipIssuer,
        string scheme = "OperatorBearer",
        string issuer = "honua-operator-bearer")
        => new(new ClaimsIdentity(
            CreateIdentityClaims(subject, issuer, membershipIssuer),
            scheme));

    private static IEnumerable<Claim> CreateIdentityClaims(
        string subject,
        string issuer,
        string? membershipIssuer)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, subject);
        yield return new Claim("iss", issuer);
        if (membershipIssuer is not null)
        {
            yield return new Claim(OperationAuthorityContext.MembershipIssuerClaimType, membershipIssuer);
        }
    }

    private static OperationProposal CreateProposal(
        string proposalId,
        OperationProposalStatus status,
        OperationAuthorityContext? authority = null,
        bool omitAuthority = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new OperationProposal
        {
            OperationInstanceId = $"opinst-{proposalId}",
            ProposalId = proposalId,
            Kind = OperationClass.Deploy,
            Status = status,
            RequestedBy = "proposer",
            Authority = omitAuthority ? null : authority ?? ValidAuthority(),
            Plan = new OperationProposalPlan { Summary = "test proposal" },
            Audit = new OperationAuditInfo { Reason = "test" },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static OperationAuthorityContext ValidAuthority() => new()
    {
        Issuer = "test-service",
        Actor = "proposer",
        Scheme = "Service",
        EffectiveTenant = "tenant-1",
        ScopeGoverned = false,
    };

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

    private sealed class LegacyStringOnlyGateway : IOperationGateway
    {
        public bool StringApprovalCalled { get; private set; }

        public Task<OperationGatewayResult> RouteAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationGatewayResult> CreateApprovalProposalAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OperationProposal?> ApplyApprovedProposalAsync(
            string proposalId,
            string approvedBy,
            CancellationToken cancellationToken = default)
        {
            StringApprovalCalled = true;
            return Task.FromResult<OperationProposal?>(null);
        }

        public Task<OperationProposal?> RejectProposalAsync(
            string proposalId,
            string rejectedBy,
            string reason,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Thread-safe in-memory proposal store with CAS semantics (matching Redis behaviour).
    /// </summary>
    private sealed class InMemoryProposalStore(
        OperationProposal proposal,
        Func<Task>? beforeGet = null,
        Func<OperationProposal, Task>? beforeTrySet = null,
        Action<OperationProposal>? afterTrySet = null) : IOperationProposalStore
    {
        private OperationProposal _proposal = proposal;
        private readonly Lock _lock = new();

        public OperationProposal Snapshot
        {
            get
            {
                lock (_lock)
                {
                    return _proposal;
                }
            }
        }

        public async Task<OperationProposal?> GetAsync(
            string proposalId,
            CancellationToken cancellationToken = default)
        {
            OperationProposal? snapshot;
            lock (_lock)
            {
                snapshot = _proposal.ProposalId == proposalId ? _proposal : null;
            }

            if (beforeGet != null)
            {
                await beforeGet();
            }

            return snapshot;
        }

        public async Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            if (beforeTrySet != null)
            {
                await beforeTrySet(proposal);
            }

            OperationProposal persisted;
            lock (_lock)
            {
                if (_proposal.ProposalId != proposal.ProposalId || _proposal.Version != proposal.Version)
                {
                    return false;
                }

                _proposal = proposal with { Version = proposal.Version + 1 };
                persisted = _proposal;
            }

            afterTrySet?.Invoke(persisted);
            return true;
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
    private sealed class RecordingExecutor(Action<OperationGatewayRequest>? onExecute = null) : IOperationExecutor
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
            onExecute?.Invoke(request);
            return Task.FromResult<string?>("exec-op-id");
        }
    }

    private sealed class BlockingExecutor : IOperationExecutor
    {
        public TaskCompletionSource<bool> Entered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public OperationClass OperationClass => OperationClass.Deploy;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(null);

        public async Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
            return "exec-op-id";
        }
    }
}
