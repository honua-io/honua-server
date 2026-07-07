// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Promotion-gate coverage for the on-prem/air-gapped fix (#2543). Proves the reconciler can promote
/// a healthy rollout on the backend's own health gate when no telemetry backend is configured
/// (self-hosted rolling default), that cloud targets still require the telemetry gate, and that the
/// explicit <c>manual</c> gate never auto-promotes.
/// </summary>
public sealed class DeployWorkflowReconcilerPromotionGateTests
{
    [Fact]
    public async Task Reconcile_SelfHostedRolling_NoTelemetry_PromotesOnBackendHealthGate()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            DeployTargetKind.SelfHostedRolling,
            "honua-yarp-rolling",
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                Message = "Standby healthy and ready for cutover."
            });
        var operation = CreateOperation(DeployTargetKind.SelfHostedRolling, "honua-yarp-rolling");
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(1, "with no telemetry policy the self-hosted health gate must drive the cutover");
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
    }

    [Fact]
    public async Task Reconcile_CloudTarget_NoTelemetry_DoesNotPromote()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            DeployTargetKind.Kubernetes,
            "honua-gitops-kubernetes",
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                Message = "Canary paused awaiting the gate."
            });
        var operation = CreateOperation(DeployTargetKind.Kubernetes, "honua-gitops-kubernetes");
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "cloud targets default to the telemetry gate and must not promote without it");
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    [Fact]
    public async Task Reconcile_ManualGate_DoesNotAutoPromoteEvenWhenHealthy()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            DeployTargetKind.SelfHostedRolling,
            "honua-yarp-rolling",
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                Message = "Standby healthy and ready for cutover."
            });
        var operation = CreateOperation(
            DeployTargetKind.SelfHostedRolling,
            "honua-yarp-rolling",
            extraParameters: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["deployment.promotion_gate"] = "manual"
            });
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "the manual gate holds until an operator promotes explicitly");
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
    }

    // ---- helpers ---------------------------------------------------------

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        RecordingDeployBackend backend)
        => new(
            store,
            new SingleTargetRegistry(backend.TargetKind, backend.BackendName),
            [backend],
            // No telemetry policy is exercised here (the operations carry no telemetry.* params), so a
            // real evaluator with empty options returns null and the promotion-gate branch decides.
            new DeployTelemetrySignalEvaluator(
                new OptionsMonitorStub(new ControlPlaneOptions()),
                [],
                NullLogger<DeployTelemetrySignalEvaluator>.Instance),
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperation(
        DeployTargetKind kind,
        string backendName,
        IReadOnlyDictionary<string, string>? extraParameters = null)
    {
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (extraParameters != null)
        {
            foreach (var (key, value) in extraParameters)
            {
                parameters[key] = value;
            }
        }

        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Baking",
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
                TargetKind = kind,
                Backend = backendName,
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = parameters
            }
        };
    }

    private sealed class OptionsMonitorStub(ControlPlaneOptions value) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => value;

        public ControlPlaneOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class RecordingDeployBackend(
        DeployTargetKind targetKind,
        string backendName,
        DeployObservation observe) : IDeployBackend
    {
        public int PromoteCalls { get; private set; }

        public string BackendName => backendName;

        public DeployTargetKind TargetKind => targetKind;

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
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.Succeeded,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Promoted"
            });
        }

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rollback requested"
            });
    }

    private sealed class SingleTargetRegistry(DeployTargetKind kind, string backendName) : IDeployTargetRegistry
    {
        private readonly DeployTargetDefinition _target = new()
        {
            TargetId = "target",
            TargetKind = kind,
            Backend = backendName,
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        };

        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>([_target]);

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult(targetId == _target.TargetId ? _target : null);
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
