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
/// Reconciler-level lease-contention coverage (#2161). The store-level lease primitive
/// (<c>TryAcquireLeaseAsync</c>) is tested elsewhere; this proves that when two distinct reconciler
/// instances (i.e. two nodes / two background loops) race the SAME operation, the per-operation
/// exclusive lease lets only ONE reconciler advance — the loser observes the lease is held and
/// no-ops, so the backend is not double-driven.
/// </summary>
public sealed class DeployWorkflowReconcilerLeaseContentionTests
{
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
