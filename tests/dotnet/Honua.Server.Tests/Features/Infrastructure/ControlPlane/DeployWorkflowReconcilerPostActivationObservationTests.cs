// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Durable post-activation observation/recovery window coverage (honua-server#4618). Proves the
/// reconciler no longer drops a promoted deploy out of reconciliation the instant cutover completes:
/// it opens a bounded, persisted observation window, keeps evaluating the same deterministic rollback
/// signals during it, and only finalizes (retiring any backend-retained recovery capacity through
/// <see cref="IDeployBackend.CompleteProtectionAsync"/>) once the window elapses without a trigger.
/// </summary>
public sealed class DeployWorkflowReconcilerPostActivationObservationTests
{
    [Fact]
    public async Task Reconcile_PromotionSucceeds_OpensObservationWindow_StaysNonTerminal()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                Message = "Standby healthy and ready for cutover."
            },
            promote: new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                Message = "Promoted"
            });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(1);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(
            WorkflowOperationStatus.Reconciling,
            "a freshly-promoted candidate must stay under observation, not drop out of reconciliation");
        updated.CompletedAt.Should().BeNull();
        updated.Deploy!.Protection.Should().NotBeNull();
        updated.Deploy.Protection!.Phase.Should().Be(DeployProtectionPhase.Observing);
        updated.Deploy.Protection.CandidateRevision.Should().Be("sha256:new");
        updated.Deploy.Protection.PreviousRevision.Should().Be("sha256:old");
        updated.Deploy.Protection.ObservationDeadline.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void BeginPostActivationObservation_StampsExposureAtCutover_AndNeverMovesAnExistingStamp()
    {
        // A backend that stages the candidate without traffic is first exposed by the cutover itself (#4617).
        var promoted = CreateOperation(WorkflowOperationStatus.Succeeded);
        var beforeCutover = DateTimeOffset.UtcNow;

        var observing = DeployWorkflowReconciler.BeginPostActivationObservationIfPromoted(promoted);

        observing.Deploy!.TrafficExposedAt.Should().NotBeNull();
        observing.Deploy.TrafficExposedAt!.Value.Should().BeOnOrAfter(beforeCutover);
        observing.Deploy.TrafficExposedAt.Should().Be(observing.Deploy.Protection!.FirstExposureAt);

        var exposedEarlier = DateTimeOffset.UtcNow.AddMinutes(-7);
        var alreadyExposed = promoted with { Deploy = promoted.Deploy! with { TrafficExposedAt = exposedEarlier } };

        DeployWorkflowReconciler.BeginPostActivationObservationIfPromoted(alreadyExposed)
            .Deploy!.TrafficExposedAt.Should().Be(exposedEarlier, "an exposure stamp is persisted once and never moved");
    }

    [Fact]
    public async Task Reconcile_ObservationWindowElapsed_FinalizesAndSucceeds()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation { Status = WorkflowOperationStatus.Reconciling },
            completeProtection: new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                Message = "Retained recovery capacity retired."
            });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling, protection: CreateProtection(
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.CompleteProtectionCalls.Should().Be(1);
        backend.PromoteCalls.Should().Be(0, "an active protection window must never re-trigger promotion");
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        updated.CompletedAt.Should().NotBeNull();
        updated.Deploy!.Protection.Should().NotBeNull();
        updated.Deploy.Protection!.Phase.Should().Be(DeployProtectionPhase.Expired);
    }

    [Fact]
    public async Task Reconcile_ObservationWindowNotYetElapsed_HoldsWithoutFinalizing()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation { Status = WorkflowOperationStatus.Reconciling });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling, protection: CreateProtection(
            deadline: DateTimeOffset.UtcNow.AddMinutes(5)));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.CompleteProtectionCalls.Should().Be(0);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        updated.Deploy!.Protection!.Phase.Should().Be(DeployProtectionPhase.Observing);
    }

    [Fact]
    public async Task Reconcile_BackendRecommendsRollbackDuringWindow_RunsDeterministicRecoveryWithoutRepromoting()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                RollbackRecommended = true,
                Message = "Candidate controller became unavailable."
            },
            rollback: new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                Message = "Repointed at the retained previous replica."
            });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling, protection: CreateProtection(
            deadline: DateTimeOffset.UtcNow.AddMinutes(5)));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.RollbackCalls.Should().Be(1, "a backend-recommended rollback signal must run deterministically without a second decision");
        backend.PromoteCalls.Should().Be(0);
        backend.CompleteProtectionCalls.Should().Be(0);
        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        updated.Deploy!.Protection.Should().NotBeNull();
        updated.Deploy.Protection!.Phase.Should().Be(DeployProtectionPhase.Recovering);
        updated.Deploy.Protection.RecoveryDeadline.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconcile_RollbackSettlesDuringWindow_ClearsProtection()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                RollbackRecommended = true,
                Message = "Candidate controller became unavailable."
            },
            rollback: new DeployObservation
            {
                Status = WorkflowOperationStatus.RolledBack,
                Message = "Previous revision confirmed serving."
            });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling, protection: CreateProtection(
            deadline: DateTimeOffset.UtcNow.AddMinutes(5)));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        updated.Deploy!.Protection.Should().BeNull("a fully settled rollback has fully recovered; there is nothing left to observe");
    }

    [Fact]
    public async Task Reconcile_CompleteProtectionFails_EscalatesToManualInterventionAsUnavailable()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            observe: new DeployObservation { Status = WorkflowOperationStatus.Reconciling },
            completeProtection: new DeployObservation
            {
                Status = WorkflowOperationStatus.Failed,
                Message = "Container runtime unreachable; could not stop the retained replica."
            });
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling, protection: CreateProtection(
            deadline: DateTimeOffset.UtcNow.AddSeconds(-1)));
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        updated.Deploy!.Protection.Should().NotBeNull();
        updated.Deploy.Protection!.Phase.Should().Be(DeployProtectionPhase.Unavailable);
    }

    // ---- helpers ---------------------------------------------------------

    private static DeployProtectionState CreateProtection(DateTimeOffset deadline)
        => new()
        {
            PreviousRevision = "sha256:old",
            CandidateRevision = "sha256:new",
            FirstExposureAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ObservationDeadline = deadline,
            PolicyDigest = "test-digest",
            Phase = DeployProtectionPhase.Observing
        };

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        RecordingDeployBackend backend)
        => new(
            store,
            new SingleTargetRegistry(),
            [backend],
            new StubDeployTelemetrySignalEvaluator(null),
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperation(
        WorkflowOperationStatus status,
        DeployProtectionState? protection = null)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Observing",
            ProviderOperationId = "recording-backend:op",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                Reason = "Rollout",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:target",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "target",
                TargetKind = DeployTargetKind.SelfHostedRolling,
                Backend = "honua-yarp-rolling",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal),
                Protection = protection
            }
        };
    }

    private sealed class StubDeployTelemetrySignalEvaluator(DeployTelemetryDecision? decision) : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
    }

    private sealed class RecordingDeployBackend(
        DeployObservation observe,
        DeployObservation? promote = null,
        DeployObservation? rollback = null,
        DeployObservation? completeProtection = null) : IDeployBackend
    {
        public int PromoteCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public int CompleteProtectionCalls { get; private set; }

        public string BackendName => "honua-yarp-rolling";

        public DeployTargetKind TargetKind => DeployTargetKind.SelfHostedRolling;

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeploySubmissionResult { Status = WorkflowOperationStatus.Submitted, Message = "Submitted" });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(observe with
            {
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision
            });

        public Task<DeployObservation> PromoteAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            PromoteCalls++;
            return Task.FromResult((promote ?? new DeployObservation { Status = WorkflowOperationStatus.Succeeded }) with
            {
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult((rollback ?? new DeployObservation { Status = WorkflowOperationStatus.RollbackRequested }) with
            {
                ProviderOperationId = operation.ProviderOperationId
            });
        }

        public Task<DeployObservation> CompleteProtectionAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            CompleteProtectionCalls++;
            return Task.FromResult((completeProtection ?? new DeployObservation { Status = WorkflowOperationStatus.Succeeded }) with
            {
                ProviderOperationId = operation.ProviderOperationId
            });
        }
    }

    private sealed class SingleTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "target",
            TargetKind = DeployTargetKind.SelfHostedRolling,
            Backend = "honua-yarp-rolling",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([Target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == Target.TargetId ? Target : null);
    }

    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryAdd(operationId, ownerId));

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
            return Task.CompletedTask;
        }

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryAdd(operation.OperationId, operation));

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

        public Task<WorkflowOperationRecord?> GetByMetadataPackageIdAsync(string packageId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.CompletedTask;
        }

        public Task<bool> TrySetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        {
            _operations[operation.OperationId] = operation;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
