// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciler-level lease-contention coverage (#2161). The store-level lease primitive
/// (<c>TryAcquireLeaseAsync</c>) is tested elsewhere; this proves that when two distinct reconciler
/// instances (i.e. two nodes / two background loops) race the SAME operation, the per-operation
/// exclusive lease lets only ONE reconciler advance — the loser observes the lease is held and
/// no-ops, so the backend is not double-driven.
/// </summary>
public sealed class DeployWorkflowReconcilerLeaseContentionTests
{
    [Theory]
    [Trait("Tier", "Fast")]
    [InlineData(WorkflowOperationStatus.Reconciling)]
    [InlineData(WorkflowOperationStatus.RollbackRequested)]
    public async Task ReconcileWorkflowOperationAsync_HostStops_ReplacementResumes(WorkflowOperationStatus status)
    {
        var store = new InMemoryWorkflowOperationStore();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new CountingObserveBackend(entered, release);
        var operation = CreateOperation() with { Status = status };
        await store.TryCreateAsync(operation);
        using var shutdown = new CancellationTokenSource();
        var source = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        var reconcile = source.ReconcileWorkflowOperationAsync(operation.OperationId, shutdown.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await reconcile;
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(status);
        (await store.GetAsync(operation.OperationId))!.CompletedAt.Should().BeNull();

        release.TrySetResult();
        var target = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        await target.ReconcileWorkflowOperationAsync(operation.OperationId);
        backend.ObserveCalls.Should().Be(2, "shutdown must release ownership for the replacement reconciler");
    }

    [Fact]
    public async Task ReconcileWorkflowOperationAsync_TwoReconcilersOneOperation_OnlyOneAdvances()
    {
        var store = new InMemoryWorkflowOperationStore();

        // The winning reconciler blocks inside ObserveAsync until the losing reconciler has had its
        // turn, making the contention deterministic: while the lease is held, the second reconciler
        // must fail to acquire and no-op.
        var observeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseObserve = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new CountingObserveBackend(observeEntered, releaseObserve);
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        var telemetry = new NullTelemetryEvaluator();
        var reconcilerA = CreateReconciler(store, backend, telemetry);
        var reconcilerB = CreateReconciler(store, backend, telemetry);

        // Start the winner; wait until it is inside ObserveAsync holding the lease.
        var runA = reconcilerA.ReconcileWorkflowOperationAsync(operation.OperationId);
        await observeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // While A holds the lease, B attempts to reconcile: it must not acquire the lease nor drive
        // the backend.
        await reconcilerB.ReconcileWorkflowOperationAsync(operation.OperationId);

        // Let A finish.
        releaseObserve.SetResult();
        await runA;

        backend.ObserveCalls.Should().Be(
            1,
            "the exclusive per-operation lease must let only one reconciler advance and drive the backend");

        store.MaxConcurrentHolders.Should().Be(1, "the lease must never be held by two reconcilers at once");
    }

    [Fact]
    public async Task ReconcileWorkflowOperationAsync_LeaseHeldByAnother_LoserNoOps()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CountingObserveBackend();
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        // Pre-acquire the lease as a different owner so the reconciler cannot get it.
        (await store.TryAcquireLeaseAsync(operation.OperationId, "another-node", TimeSpan.FromSeconds(30)))
            .Should().BeTrue();

        var reconciler = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);

        backend.ObserveCalls.Should().Be(0, "a reconciler that cannot acquire the lease must not drive the backend");
    }

    [Fact]
    public async Task Checkpoint_PersistsMigrationAndRevisionsBeforeActivation()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CheckpointBackend(store);
        var operation = CreateOperation() with { Status = WorkflowOperationStatus.Submitted, CurrentPhase = ICheckpointedDeployBackend.PendingPhase };
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var prepared = (await store.GetAsync(operation.OperationId))!;
        prepared.CurrentPhase.Should().Be(ICheckpointedDeployBackend.PreparedPhase);
        prepared.ProviderOperationId.Should().Be("candidate");
        prepared.Deploy!.CurrentRevision.Should().Be("stable");
        prepared.ObservedState.Should().Be("original");
        backend.ActivationCount.Should().Be(0);
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        backend.ActivationCount.Should().Be(1);
        var activated = (await store.GetAsync(operation.OperationId))!;
        activated.CurrentPhase.Should().Be(ICheckpointedDeployBackend.ActivatedPhase);
        activated.Deploy!.TrafficExposedAt.Should().NotBeNull();
        activated.Deploy.TrafficExposedAt.Should().BeAfter(operation.CreatedAt);
    }

    [Fact]
    public async Task Checkpoint_InterruptedMigrationDoesNotRepeatOrActivate()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CheckpointBackend(store) { InterruptPreparation = true };
        var operation = CreateOperation() with { Status = WorkflowOperationStatus.Submitted, CurrentPhase = ICheckpointedDeployBackend.PendingPhase };
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        using var shutdown = new CancellationTokenSource();
        var running = reconciler.ReconcileWorkflowOperationAsync(operation.OperationId, shutdown.Token);
        await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        shutdown.Cancel();
        await running;
        (await store.GetAsync(operation.OperationId))!.CurrentPhase.Should().Be(ICheckpointedDeployBackend.PreparingPhase);
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.ManualInterventionRequired);
        backend.PreparationCount.Should().Be(1);
        backend.ActivationCount.Should().Be(0);
    }

    [Fact]
    public async Task Checkpoint_StagingDoesNotStampExposureUntilTrafficMoves()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CheckpointBackend(store) { StageOneObservation = true };
        var original = CreateOperation();
        var operation = original with
        {
            Status = WorkflowOperationStatus.Submitted,
            CurrentPhase = ICheckpointedDeployBackend.ActivatedPhase,
            Deploy = original.Deploy! with { CanaryRamp = new CanaryRampSpec { StepWeights = [10, 100], StepBakeDuration = TimeSpan.FromSeconds(1) } }
        };
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(store, backend, new NullTelemetryEvaluator());
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var staged = (await store.GetAsync(operation.OperationId))!;
        staged.Deploy!.TrafficExposedAt.Should().BeNull();
        staged.Deploy.CanaryRamp!.CurrentStepStartedAt.Should().BeNull();
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        (await store.GetAsync(operation.OperationId))!.Deploy!.TrafficExposedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Checkpoint_FailedCanaryRampRequestsRollback()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CheckpointBackend(store) { FailRamp = true };
        var original = CreateOperation();
        var operation = original with
        {
            Deploy = original.Deploy! with
            {
                CanaryRamp = new CanaryRampSpec { StepWeights = [10, 50, 100], StepBakeDuration = TimeSpan.FromSeconds(1) }
            }
        };
        var result = await DeployWorkflowReconciler.AdvanceOrPromoteAsync(operation, backend, CancellationToken.None);
        backend.RollbackCount.Should().Be(1);
        result.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        result.Deploy!.CanaryRamp!.CurrentStepIndex.Should().Be(0);
    }

    [Fact]
    public async Task ProviderRollbackRecommendation_DoesNotWaitForTelemetry()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new CheckpointBackend(store) { RecommendRollback = true };
        var operation = CreateOperation() with { CurrentPhase = ICheckpointedDeployBackend.ActivatedPhase };
        await store.TryCreateAsync(operation);
        var reconciler = CreateReconciler(store, backend, new FailingTelemetryEvaluator());
        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        backend.RollbackCount.Should().Be(1);
        (await store.GetAsync(operation.OperationId))!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
    }

    // ---- helpers ---------------------------------------------------------

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

    private static WorkflowOperationRecord CreateOperation()
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Reconciling rollout",
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

    private sealed class CountingObserveBackend(
        TaskCompletionSource? observeEntered = null,
        TaskCompletionSource? releaseObserve = null) : IDeployBackend
    {
        private int _observeCalls;

        public int ObserveCalls => Volatile.Read(ref _observeCalls);

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
                ProviderOperationId = $"counting-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public async Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _observeCalls);
            observeEntered?.TrySetResult();
            if (releaseObserve != null)
            {
                await releaseObserve.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Observed succeeded"
            };
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class CheckpointBackend(InMemoryWorkflowOperationStore store) : IDeployBackend, ICheckpointedDeployBackend
    {
        public bool InterruptPreparation { get; init; }
        public bool RecommendRollback { get; init; }
        public bool FailRamp { get; init; }
        public bool StageOneObservation { get; init; }
        public int ObserveCount { get; private set; }
        public int RollbackCount { get; private set; }
        public int PreparationCount { get; private set; }
        public int ActivationCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string BackendName => "honua-gitops-kubernetes";
        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;
        public bool RequiresCheckpoint(WorkflowOperationRecord operation) => true;
        public async Task<DeployCheckpoint> PrepareCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
        {
            PreparationCount++;
            (await store.GetAsync(operation.OperationId, cancellationToken))!.CurrentPhase.Should().Be(ICheckpointedDeployBackend.PreparingPhase);
            Entered.TrySetResult();
            if (InterruptPreparation) await Task.Delay(Timeout.Infinite, cancellationToken);
            return new DeployCheckpoint { CandidateRevision = "candidate", StableRevision = "stable", OriginalServiceRevision = "original" };
        }
        public async Task<DeployObservation> ActivateCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
        {
            var saved = (await store.GetAsync(operation.OperationId, cancellationToken))!;
            saved.CurrentPhase.Should().Be(ICheckpointedDeployBackend.PreparedPhase);
            saved.ProviderOperationId.Should().Be("candidate");
            saved.Deploy!.CurrentRevision.Should().Be("stable");
            ActivationCount++;
            return new DeployObservation { Status = WorkflowOperationStatus.Reconciling };
        }
        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => Task.FromResult(new DeployBackendCapabilities());
        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default) => Task.FromResult(new DeployPlan { IsReadyToSubmit = true });
        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => FailRamp ? Task.FromResult(new DeploySubmissionResult { Status = WorkflowOperationStatus.Failed }) : throw new NotSupportedException();
        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            ObserveCount++;
            return Task.FromResult(new DeployObservation
            {
                Status = StageOneObservation && ObserveCount == 1 ? WorkflowOperationStatus.Submitted : WorkflowOperationStatus.Reconciling,
                RollbackRecommended = RecommendRollback
            });
        }
        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackCount++;
            return Task.FromResult(new DeployObservation { Status = WorkflowOperationStatus.RollbackRequested });
        }
    }

    private sealed class FailingTelemetryEvaluator : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Telemetry is unavailable.");
    }

    private sealed class NullTelemetryEvaluator : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(
            WorkflowOperationRecord operation,
            CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTelemetryDecision?>(null);
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

    /// <summary>
    /// In-memory store with a real exclusive lease (TryAdd admits one owner) plus instrumentation that
    /// records the maximum number of simultaneous lease holders so the test can assert exclusivity.
    /// </summary>
    private sealed class InMemoryWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);
        private int _currentHolders;
        private int _maxConcurrentHolders;

        public int MaxConcurrentHolders => Volatile.Read(ref _maxConcurrentHolders);

        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            var acquired = _leases.TryAdd(operationId, ownerId);
            if (acquired)
            {
                var holders = Interlocked.Increment(ref _currentHolders);
                UpdateMax(holders);
            }

            return Task.FromResult(acquired);
        }

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
        {
            if (_leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId)))
            {
                Interlocked.Decrement(ref _currentHolders);
            }

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

        private void UpdateMax(int holders)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxConcurrentHolders);
                if (holders <= observed)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maxConcurrentHolders, holders, observed) != observed);
        }
    }
}
