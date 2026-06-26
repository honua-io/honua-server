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
/// End-to-end health-gated rollback coverage (#1849 / #2163). Drives the REAL
/// <see cref="DeployTelemetrySignalEvaluator"/> with a synthetic <c>/healthz/ready</c> probe through
/// the <see cref="DeployWorkflowReconciler"/> against an arbitrary deploy backend, proving the
/// health gate is inherited by the backend rollout path (no hard-flip): an unhealthy probe drives the
/// SAME automatic-rollback path as an error-rate/latency breach, and a healthy probe promotes.
/// </summary>
public sealed class DeployWorkflowReconcilerHealthGateTests
{
    [Fact]
    public async Task Reconcile_HealthProbeUnhealthy_DrivesBackendRollback()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Succeeded);
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(
            new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 3 }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.RollbackCalls.Should().Be(1, "an unhealthy synthetic health probe must trigger the backend rollback");
        updated!.Status.Should().BeOneOf(
            WorkflowOperationStatus.RollbackRequested,
            WorkflowOperationStatus.RolledBack);
        updated.CurrentPhase.Should().Contain("health probe is unhealthy");
    }

    [Fact]
    public async Task Reconcile_HealthProbeHealthy_DoesNotRollBack()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(observeStatus: WorkflowOperationStatus.Succeeded);
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);

        var evaluator = CreateRealEvaluator(
            new FakeHealthProbe(new DeployHealthProbeResult { Attempts = 3, Failures = 0 }));
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        backend.RollbackCalls.Should().Be(0, "a healthy synthetic health probe must not trigger a rollback");
        updated!.Status.Should().NotBe(WorkflowOperationStatus.RollbackRequested);
    }

    // ---- helpers ---------------------------------------------------------

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        IDeployBackend backend,
        IDeployTelemetrySignalEvaluator evaluator)
        => new(
            store,
            new SingleTargetRegistry(),
            [backend],
            evaluator,
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static DeployTelemetrySignalEvaluator CreateRealEvaluator(IDeployHealthProbe probe)
        => new(
            new OptionsMonitorStub(new ControlPlaneOptions()),
            [],
            NullLogger<DeployTelemetrySignalEvaluator>.Instance,
            probe);

    private static WorkflowOperationRecord CreateOperation()
    {
        // Created comfortably past the default 2-minute warmup so the gate evaluates this cycle.
        var now = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Baking canary",
            ProviderOperationId = "recording-backend:op",
            Audit = new OperationAuditInfo
            {
                RequestedBy = "alice",
                Reason = "Canary",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-k8s",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-k8s",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    // Health-only gate: the probe short-circuits before any metrics connection is
                    // resolved, so no metrics backend is required to prove the rollback path.
                    ["telemetry.connection"] = "prod-prom",
                    ["telemetry.healthz.url"] = "https://example.com/healthz/ready"
                }
            }
        };
    }

    private sealed class FakeHealthProbe(DeployHealthProbeResult result) : IDeployHealthProbe
    {
        public Task<DeployHealthProbeResult> ProbeAsync(DeployHealthProbeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(result);
    }

    private sealed class OptionsMonitorStub(ControlPlaneOptions value) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => value;

        public ControlPlaneOptions Get(string? name) => value;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class RecordingDeployBackend(WorkflowOperationStatus observeStatus) : IDeployBackend
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
                ProviderOperationId = $"recording-backend:{operation.OperationId}",
                Message = "Submitted"
            });

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = observeStatus,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = "Observed"
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            RollbackCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
        }
    }

    private sealed class SingleTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-k8s",
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
