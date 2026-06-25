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
/// Failure-of-failure coverage (#2161) for the deploy reconciler's rollback escalation: when the
/// telemetry gate decides a rollback is required but the provider backend cannot honour it (returns
/// <see cref="WorkflowOperationStatus.Failed"/> or echoes a non-terminal status on a transient
/// error — as the Lambda/ECS/Argo backends do), the reconciler must escalate to
/// <see cref="WorkflowOperationStatus.ManualInterventionRequired"/> rather than parking the deploy in
/// a never-terminal loop with the degraded revision still live.
/// </summary>
public sealed class DeployWorkflowReconcilerRollbackFailureTests
{
    [Fact]
    public async Task Reconciler_WhenRollbackBackendReturnsFailed_EscalatesToManualIntervention()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new FailingRollbackBackend(
            rollbackStatus: WorkflowOperationStatus.Failed,
            rollbackMessage: "Lambda alias rollback failed due to a transient AWS error.");
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Automatic rollback requested because telemetry detected canary degradation."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        updated.CompletedAt.Should().NotBeNull();
        updated.CurrentPhase.Should().Contain("manual intervention");
        updated.ErrorMessage.Should().Contain("Automatic rollback failed");
        backend.RollbackCalls.Should().Be(1);
    }

    [Fact]
    public async Task Reconciler_WhenRollbackBackendReturnsNonTerminal_ReDrivesOrEscalates_DoesNotStickForever()
    {
        // The Lambda/ECS backends echo the operation's CURRENT status on a transient failure. Because
        // the rollback path is entered from a non-RollbackRequested status, a returned status that is
        // neither RollbackRequested nor RolledBack (here: Reconciling) means the rollback did not take.
        // Driving the reconciler repeatedly must reach a terminal manual-intervention state rather than
        // looping forever in a non-terminal status.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new FailingRollbackBackend(
            rollbackStatus: WorkflowOperationStatus.Reconciling,
            rollbackMessage: "Rollback could not be applied; provider is still reconciling.");
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Automatic rollback requested because telemetry detected canary degradation."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        WorkflowOperationRecord? updated = null;
        for (var cycle = 0; cycle < 5; cycle++)
        {
            await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
            updated = await store.GetAsync(operation.OperationId);
            if (updated is not null && IsTerminal(updated.Status))
            {
                break;
            }
        }

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(
            WorkflowOperationStatus.ManualInterventionRequired,
            "a rollback that never settles must escalate to a terminal manual-intervention state, not loop forever");
        updated.CompletedAt.Should().NotBeNull();
        updated.ErrorMessage.Should().Contain("Automatic rollback failed");
        backend.RollbackCalls.Should().Be(1, "once escalated to a terminal state the reconciler stops re-driving");
    }

    [Fact]
    public async Task Reconciler_WhenRollbackBackendSucceeds_RemainsRollbackRequested()
    {
        // Control: a backend that honours the rollback (returns RollbackRequested) must NOT be
        // escalated to manual intervention — the escalation is strictly for failed/non-terminal
        // rollbacks.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new FailingRollbackBackend(
            rollbackStatus: WorkflowOperationStatus.RollbackRequested,
            rollbackMessage: "Rollback requested.");
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Automatic rollback requested because telemetry detected canary degradation."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        updated.CurrentPhase.Should().Contain("telemetry detected canary degradation");
    }

    // ---- helpers ---------------------------------------------------------

    private static bool IsTerminal(WorkflowOperationStatus status)
        => status is WorkflowOperationStatus.Succeeded
            or WorkflowOperationStatus.Failed
            or WorkflowOperationStatus.RolledBack
            or WorkflowOperationStatus.ManualInterventionRequired;

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        IDeployBackend backend,
        IDeployTelemetrySignalEvaluator telemetryEvaluator)
        => new(
            store,
            new SingleTargetRegistry(),
            [backend],
            telemetryEvaluator,
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperation(WorkflowOperationStatus status)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Routing canary traffic",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                Reason = "Canary",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-ecs",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-ecs",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            }
        };
    }

    private sealed class FailingRollbackBackend(
        WorkflowOperationStatus rollbackStatus,
        string rollbackMessage) : IDeployBackend
    {
        public int RollbackCalls { get; private set; }

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

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
            => Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"failing-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = operation.Status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = operation.CurrentPhase
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = rollbackStatus,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = rollbackMessage
            });
        }
    }

    private sealed class SingleTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-ecs",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
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

    private sealed class StubDeployTelemetrySignalEvaluator(DeployTelemetryDecision? decision) : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(decision);
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

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
        {
            var operations = _operations.Values
                .Where(operation => !kind.HasValue || operation.Kind == kind.Value)
                .ToArray();
            return Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(operations);
        }
    }
}
