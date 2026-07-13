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
/// Failure-of-failure coverage (#2161) for the deploy reconciler's rollback escalation. When the
/// telemetry gate decides a rollback is required but the provider backend cannot honour it, the
/// reconciler distinguishes two failure shapes:
/// <list type="bullet">
/// <item>A TERMINAL/hard failure (the backend returns <see cref="WorkflowOperationStatus.Failed"/> or
/// <see cref="WorkflowOperationStatus.ManualInterventionRequired"/>) escalates immediately.</item>
/// <item>A TRANSIENT failure (the Lambda backend echoes the operation's pre-rollback status on a
/// throttle/transient AWS error) is retried on a bounded budget — a single blip must NOT page an
/// operator — and escalates only once the budget is exhausted.</item>
/// </list>
/// Either way the deploy must reach a terminal manual-intervention state rather than parking in a
/// never-terminal loop with the degraded revision still live.
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
    public async Task Reconciler_WhenRollbackBackendTransientlyFailsForever_RetriesThenEscalates_DoesNotStickForever()
    {
        // The Lambda backend echoes the operation's CURRENT status (here: Reconciling) with a "will
        // retry" message on a TRANSIENT provider error. The reconciler must NOT escalate on the first
        // blip — it retries on a bounded budget. But a rollback that transiently fails on EVERY cycle
        // is genuinely stuck: once the budget is exhausted the reconciler must escalate to a terminal
        // manual-intervention state rather than looping forever (the original #2161 bug).
        var store = new InMemoryWorkflowOperationStore();
        var backend = new FailingRollbackBackend(
            rollbackStatus: WorkflowOperationStatus.Reconciling,
            rollbackMessage: "Lambda alias rollback failed due to a transient AWS error.");
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Automatic rollback requested because telemetry detected canary degradation."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        WorkflowOperationRecord? updated = null;
        var firstCycleStatus = WorkflowOperationStatus.Planned;
        for (var cycle = 0; cycle < 10; cycle++)
        {
            await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
            updated = await store.GetAsync(operation.OperationId);
            if (cycle == 0)
            {
                firstCycleStatus = updated!.Status;
            }

            if (updated is not null && IsTerminal(updated.Status))
            {
                break;
            }
        }

        firstCycleStatus.Should().Be(
            WorkflowOperationStatus.Reconciling,
            "the first transient blip must NOT page an operator — it stays re-drivable and retries");

        if (updated is null)
        {
            Assert.Fail("Reconciliation loop never produced an updated operation record.");
            return;
        }

        updated.Status.Should().Be(
            WorkflowOperationStatus.ManualInterventionRequired,
            "a rollback that never settles after the bounded retries must escalate, not loop forever");
        updated.CompletedAt.Should().NotBeNull();
        updated.ErrorMessage.Should().Contain("Automatic rollback failed after");
        // Default budget is 3 attempts (DeployWorkflowReconciler.DefaultRollbackRetryBudget).
        backend.RollbackCalls.Should().Be(3, "the rollback is retried up to the bounded budget before escalating");
    }

    [Fact]
    public async Task Reconciler_WhenRollbackBackendTransientlyFailsThenSucceeds_RetriesAndSettles_DoesNotEscalate()
    {
        // A single transient throttle on a rollback must self-heal: the first cycle echoes the
        // pre-rollback status (Reconciling) and the reconciler retries; the next cycle the backend
        // honours the rollback (RollbackRequested). The operation must settle WITHOUT ever escalating
        // to manual intervention — a transient blip does not page an operator.
        var store = new InMemoryWorkflowOperationStore();
        var backend = new FailingRollbackBackend(
            rollbackStatusSequence:
            [
                WorkflowOperationStatus.Reconciling,   // transient blip
                WorkflowOperationStatus.RollbackRequested // honoured on retry
            ],
            rollbackMessage: "Rollback transiently failed, then took.");
        var operation = CreateOperation(WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            RollbackRecommended = true,
            Message = "Automatic rollback requested because telemetry detected canary degradation."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        // Cycle 1: transient failure -> stays re-drivable, does not escalate.
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var afterFirst = await store.GetAsync(operation.OperationId);
        afterFirst.Should().NotBeNull();
        afterFirst!.Status.Should().Be(
            WorkflowOperationStatus.Reconciling,
            "a transient rollback failure must not escalate on the first blip");
        afterFirst.CompletedAt.Should().BeNull();
        afterFirst.CurrentPhase.Should().Contain("will be retried");

        // Cycle 2: rollback honoured -> settles to RollbackRequested, never manual intervention.
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var afterSecond = await store.GetAsync(operation.OperationId);
        afterSecond.Should().NotBeNull();
        afterSecond!.Status.Should().Be(
            WorkflowOperationStatus.RollbackRequested,
            "once the transient error clears the rollback takes and the operation settles");
        afterSecond.Status.Should().NotBe(WorkflowOperationStatus.ManualInterventionRequired);
        backend.RollbackCalls.Should().Be(2, "one retry after the transient blip");
        // The transient-attempt counter is cleared once the rollback settles.
        afterSecond.Deploy!.Parameters.Should().NotContainKey(
            DeployWorkflowReconciler.RollbackTransientAttemptsParameterKey);
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

    private sealed class FailingRollbackBackend : IDeployBackend
    {
        private readonly IReadOnlyList<WorkflowOperationStatus> _rollbackStatusSequence;
        private readonly string _rollbackMessage;

        public FailingRollbackBackend(WorkflowOperationStatus rollbackStatus, string rollbackMessage)
            : this([rollbackStatus], rollbackMessage)
        {
        }

        public FailingRollbackBackend(
            IReadOnlyList<WorkflowOperationStatus> rollbackStatusSequence,
            string rollbackMessage)
        {
            _rollbackStatusSequence = rollbackStatusSequence;
            _rollbackMessage = rollbackMessage;
        }

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
            // Walk the configured status sequence, pinning the last entry once exhausted so a
            // single-status backend keeps returning the same status on every call.
            var index = Math.Min(RollbackCalls, _rollbackStatusSequence.Count - 1);
            var status = _rollbackStatusSequence[index];
            RollbackCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = _rollbackMessage
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
