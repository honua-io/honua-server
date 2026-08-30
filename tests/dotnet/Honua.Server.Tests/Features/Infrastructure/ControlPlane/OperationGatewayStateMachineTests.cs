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
using Honua.Server.Features.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Regression coverage for the OperationGateway state-machine gaps identified in
/// BH6-032 and BH6-033.
///
/// BH6-032: <see cref="OperationGateway"/> ExecuteDirectAsync silently returned
/// <see cref="OperationGatewayOutcome.Executed"/> when no <see cref="IOperationExecutor"/>
/// was registered for the requested <see cref="OperationClass"/>, causing Community/Pro
/// editions to silently do nothing for MetadataRelease and Seed operations.
///
/// BH6-033: <see cref="OperationGateway"/> ApplyApprovedProposalAsync left the proposal
/// in the non-terminal <see cref="OperationProposalStatus.Submitted"/> state when no
/// executor was registered for the operation class, preventing the reconciler from
/// ever advancing the proposal.
/// </summary>
public sealed class OperationGatewayStateMachineTests
{
    // ── BH6-032: ExecuteDirectAsync with unregistered operation class ─────────

    [Theory]
    [InlineData(OperationClass.MetadataRelease)]
    [InlineData(OperationClass.Seed)]
    public async Task ExecuteDirect_WithUnregisteredKind_ReturnsNotSupported(OperationClass unregisteredKind)
    {
        // Arrange: gateway has only a Deploy executor; MetadataRelease/Seed have no executor.
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(unregisteredKind).Returns(
            new GuardrailDecision(GuardrailTier.DirectExecute, unregisteredKind, HonuaEdition.Community, "DefaultGuardrailLadder"));

        var sut2 = BuildGateway(
            store: new InMemoryProposalStore(),
            executor: new DeployExecutor(),
            ladder: ladder);

        var request = new OperationGatewayRequest
        {
            Kind = unregisteredKind,
            RequestedBy = "test-user"
        };

        // Act
        var result = await sut2.RouteAsync(request);

        // Assert: must NOT claim Executed (BH6-032) — nothing ran
        result.Outcome.Should().Be(OperationGatewayOutcome.NotSupported,
            "a kind with no registered executor must not be claimed as Executed");
        result.ExecutionOperationId.Should().BeNull("no operation was performed");
    }

    [Fact]
    public async Task ExecuteDirect_WithRegisteredKind_ReturnsExecuted()
    {
        // Sanity: a kind WITH a registered executor still returns Executed.
        var ladder = Substitute.For<IGuardrailLadder>();
        ladder.Resolve(OperationClass.Deploy).Returns(
            new GuardrailDecision(GuardrailTier.DirectExecute, OperationClass.Deploy, HonuaEdition.Pro, "DefaultGuardrailLadder"));

        var sut = BuildGateway(
            store: new InMemoryProposalStore(),
            executor: new DeployExecutor(),
            ladder: ladder);

        var request = new OperationGatewayRequest
        {
            Kind = OperationClass.Deploy,
            RequestedBy = "test-user"
        };

        var result = await sut.RouteAsync(request);

        result.Outcome.Should().Be(OperationGatewayOutcome.Executed);
        result.ExecutionOperationId.Should().Be(DeployExecutor.OperationId);
    }

    // ── BH6-033: ApplyApprovedProposalAsync with unregistered operation class ─

    [Theory]
    [InlineData(OperationClass.MetadataRelease)]
    [InlineData(OperationClass.Seed)]
    public async Task ApplyApprovedProposal_WithUnregisteredKind_FailsToTerminalStatus(OperationClass unregisteredKind)
    {
        // Arrange: proposal for an unregistered operation class in AwaitingApproval.
        var proposal = CreateProposal("p-no-executor", unregisteredKind, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        // No executor for MetadataRelease/Seed — only Deploy is registered.
        var sut = BuildGateway(store: store, executor: new DeployExecutor());

        // Act
        var result = await sut.ApplyApprovedProposalAsync("p-no-executor", "admin");

        // Assert BH6-033: must transition to terminal Failed, not stay Submitted
        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Failed,
            "a proposal whose operation class has no registered executor must fail terminally, " +
            "not stay in non-terminal Submitted status (BH6-033)");
        result.ExecutionOperationId.Should().BeNull("no operation was performed");

        // The failure message should be persisted in BlockingReasons
        result.Plan.BlockingReasons.Should().ContainMatch("*No executor*",
            "the failure reason must be recorded so operators understand why the proposal failed");
    }

    [Fact]
    public async Task ApplyApprovedProposal_WithRegisteredQueuedKind_TransitionsToSubmitted()
    {
        // Sanity: a kind WITH a registered executor still resolves to Submitted.
        var proposal = CreateProposal("p-deploy", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var sut = BuildGateway(store: store, executor: new DeployExecutor());

        var result = await sut.ApplyApprovedProposalAsync("p-deploy", "admin");

        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Submitted);
        result.ExecutionOperationId.Should().Be(DeployExecutor.OperationId);
    }

    [Fact]
    public async Task ApplyApprovedProposal_WithRegisteredSynchronousKind_TransitionsToSucceeded()
    {
        var proposal = CreateProposal("p-seed", OperationClass.Seed, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var sut = BuildGateway(store: store, executor: new SynchronousExecutor());

        var result = await sut.ApplyApprovedProposalAsync("p-seed", "admin");

        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Succeeded);
        result.ExecutionOperationId.Should().Be(SynchronousExecutor.OperationId);
    }

    [Fact]
    public async Task ApplyApprovedProposal_WhenExecutorThrows_ReachesTerminalFailed()
    {
        // Executor exceptions must also yield terminal Failed (pre-existing behaviour;
        // confirmed to still hold after the BH6-033 fix).
        var proposal = CreateProposal("p-throws", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var sut = BuildGateway(store: store, executor: new ThrowingExecutor());

        var result = await sut.ApplyApprovedProposalAsync("p-throws", "admin");

        result.Should().NotBeNull();
        result!.Status.Should().Be(OperationProposalStatus.Failed);
        result.ExecutionOperationId.Should().BeNull();
    }

    [Fact]
    public async Task ApplyApprovedProposal_PreActuationCancellation_CompensatesClaimToCancelled()
    {
        var proposal = CreateProposal("p-cancel", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var executor = new CancelingValidationExecutor();
        var sut = BuildGateway(
            store: store,
            executor: executor,
            configure: services => services.AddSingleton<
                Honua.Core.Features.Operations.Abstractions.IOperationApprovalRequestMapper>(
                new CancelingReplayMapper()));

        var apply = () => sut.ApplyApprovedProposalAsync("p-cancel", "admin");

        await apply.Should().ThrowAsync<OperationCanceledException>();
        var persisted = await store.GetAsync("p-cancel");
        persisted!.Status.Should().Be(OperationProposalStatus.Cancelled);
        persisted.ResolutionReason.Should().Contain("before actuation");
        executor.ExecuteCount.Should().Be(0);
    }

    [Fact]
    public async Task ApplyApprovedProposal_FailedReplay_AuditsFailureRatherThanAppliedSuccess()
    {
        var proposal = CreateProposal("p-failed-audit", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var store = new InMemoryProposalStore(proposal);
        var audit = new RecordingAuditLog();
        var sut = BuildGateway(
            store: store,
            executor: new ThrowingExecutor(),
            configure: services => services.AddSingleton<IAuditLog>(audit));

        var result = await sut.ApplyApprovedProposalAsync("p-failed-audit", "admin");

        result!.Status.Should().Be(OperationProposalStatus.Failed);
        audit.Events.Should().Contain(item =>
            item.Action == "operation.apply_failed" && item.Outcome == AuditOutcome.Failure);
        audit.Events.Should().NotContain(item =>
            item.Action == "operation.applied" && item.Outcome == AuditOutcome.Success);
    }

    [Fact]
    public async Task ApplyApprovedProposal_TamperedPersistedPlan_FailsWithoutActuation()
    {
        var original = CreateProposal("p-tampered", OperationClass.Deploy, OperationProposalStatus.AwaitingApproval);
        var tampered = original with
        {
            Plan = original.Plan with { ExecutionPayload = "caller-supplied-replacement" },
        };
        var store = new InMemoryProposalStore(tampered);
        var executor = new RecordingExecutor();
        var sut = BuildGateway(store: store, executor: executor);

        var result = await sut.ApplyApprovedProposalAsync("p-tampered", "admin");

        result!.Status.Should().Be(OperationProposalStatus.Failed);
        result.Plan.BlockingReasons.Should().ContainMatch("*proof did not match*");
        executor.ExecuteCount.Should().Be(0);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static OperationProposal CreateProposal(
        string proposalId,
        OperationClass kind,
        OperationProposalStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = new OperationProposalPlan { Summary = "test proposal" };
        return new OperationProposal
        {
            ProposalId = proposalId,
            Kind = kind,
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
        };
    }

    private static OperationGateway BuildGateway(
        IOperationProposalStore store,
        IOperationExecutor? executor = null,
        IGuardrailLadder? ladder = null,
        Action<IServiceCollection>? configure = null)
    {
        var resolvedLadder = ladder ?? Substitute.For<IGuardrailLadder>();
        IEnumerable<IOperationExecutor> executors = executor != null
            ? [executor]
            : [];
        return CanonicalOperationGatewayTestComposition.Build(store, resolvedLadder, executors, configure);
    }

    /// <summary>
    /// Executor that handles only <see cref="OperationClass.Deploy"/> and records a fixed operation ID.
    /// </summary>
    private sealed class DeployExecutor : IOperationExecutor
    {
        public const string OperationId = "deploy-op-id";

        public OperationClass OperationClass => OperationClass.Deploy;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(null);

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(OperationId);
    }

    /// <summary>Executor that always throws on Execute.</summary>
    private sealed class ThrowingExecutor : IOperationExecutor
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
            => throw new InvalidOperationException("Simulated executor failure.");
    }

    private sealed class SynchronousExecutor : IOperationExecutor
    {
        public const string OperationId = "seed-operation-id";

        public OperationClass OperationClass => OperationClass.Seed;

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(request.Plan);

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(OperationId);
    }

    private sealed class RecordingExecutor : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;

        public int ExecuteCount { get; private set; }

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<OperationProposalPlan?>(request.Plan);

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult<string?>("unexpected");
        }
    }

    private sealed class CancelingValidationExecutor : IOperationExecutor
    {
        public OperationClass OperationClass => OperationClass.Deploy;

        public int ExecuteCount { get; private set; }

        public Task<OperationProposalPlan?> PlanAsync(
            OperationGatewayRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<OperationProposalPlan?>(new OperationCanceledException(cancellationToken));

        public Task<string?> ExecuteAsync(
            OperationGatewayRequest request,
            string? executionPayload,
            CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class CancelingReplayMapper
        : Honua.Core.Features.Operations.Abstractions.IOperationApprovalRequestMapper
    {
        public string OperationId => "control-plane.deploy";

        public OperationGatewayRequest Map(
            Honua.Core.Features.Operations.Abstractions.IOperationDescriptor descriptor,
            Honua.Core.Features.Operations.Domain.OperationRequest request,
            Honua.Core.Features.Operations.Domain.OperationPolicyContext context,
            Honua.Core.Features.Operations.Domain.PolicyDecision decision)
            => throw new NotSupportedException();

        public Honua.Core.Features.Operations.Abstractions.OperationApprovalReplayMapping MapReplay(
            OperationGatewayRequest request)
            => throw new OperationCanceledException();
    }

    private sealed class RecordingAuditLog : IAuditLog
    {
        public List<AuditEvent> Events { get; } = [];

        public Task<string?> RecordAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return Task.FromResult<string?>($"audit-{Events.Count}");
        }
    }

    /// <summary>
    /// Thread-safe in-memory proposal store with CAS semantics, shared with
    /// <see cref="OperationGatewayApprovalConcurrencyTests"/>.
    /// </summary>
    private sealed class InMemoryProposalStore(OperationProposal? initialProposal = null) : IOperationProposalStore
    {
        private OperationProposal? _proposal = initialProposal;
        private readonly Lock _lock = new();

        public Task<OperationProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                return Task.FromResult(
                    _proposal?.ProposalId == proposalId ? _proposal : (OperationProposal?)null);
            }
        }

        public Task<bool> TrySetAsync(
            OperationProposal proposal,
            TimeSpan? ttl = null,
            CancellationToken cancellationToken = default)
        {
            lock (_lock)
            {
                if (_proposal?.ProposalId != proposal.ProposalId || _proposal?.Version != proposal.Version)
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
        {
            lock (_lock)
            {
                if (_proposal != null)
                {
                    return Task.FromResult(false);
                }

                _proposal = proposal with { Version = 1 };
                return Task.FromResult(true);
            }
        }

        public Task<IReadOnlyList<OperationProposal>> ListActiveAsync(
            OperationClass? kind = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<OperationProposal>>(
                _proposal == null ? [] : [_proposal]);

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
}
