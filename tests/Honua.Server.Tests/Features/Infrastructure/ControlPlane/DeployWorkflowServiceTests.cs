// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class DeployWorkflowServiceTests
{
    [Fact]
    public async Task CreateAsync_WithDuplicateIdempotencyKey_StartsBackendOnlyOnce()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new BlockingDeployBackend();
        var service = CreateService(store, backend);

        var firstCreate = service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Initial submit",
            "release / 2026#03 ? candidate",
            "corr-1",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        await backend.StartEntered.WaitAsync(TimeSpan.FromSeconds(5));

        var secondCreate = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            "sha256:old",
            "alice",
            "Duplicate submit",
            "release / 2026#03 ? candidate",
            "corr-2",
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        secondCreate.Should().NotBeNull();
        secondCreate!.Status.Should().Be(WorkflowOperationStatus.Planned);

        backend.AllowStart();
        var firstResult = await firstCreate;

        firstResult.Should().NotBeNull();
        firstResult!.Status.Should().Be(WorkflowOperationStatus.Submitted);
        secondCreate.OperationId.Should().Be(firstResult.OperationId);
        backend.StartCount.Should().Be(1);

        var persisted = await store.GetAsync(firstResult.OperationId);
        persisted!.ProviderOperationId.Should().Be($"test-backend:{firstResult.OperationId}");
    }

    [Fact]
    public async Task CreateAsync_WhenStoreRejectsCreate_DoesNotStartBackend()
    {
        var store = new TestWorkflowOperationStore
        {
            AllowCreate = false
        };
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);

        var act = () => service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            null,
            "alice",
            "Initial submit",
            "reject-me",
            null,
            OperationPriority.Normal,
            submitImmediately: true,
            parameterOverrides: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*durably record*");
        backend.StartCount.Should().Be(0);
    }

    [Fact]
    public async Task CreateAsync_NormalizesUnsafeIdempotencyKeysIntoSafeOperationIds()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);

        var operation = await service.CreateAsync(
            "prod-api",
            "sha256:abc123",
            null,
            "alice",
            "Normalize key",
            " release / 2026#03 ? candidate ",
            null,
            OperationPriority.Normal,
            submitImmediately: false,
            parameterOverrides: null);

        operation.Should().NotBeNull();
        operation!.OperationId.Should().MatchRegex("^deploy-[a-z0-9-]+$");
        operation.OperationId.Should().NotContain("/");
        operation.OperationId.Should().NotContain("?");
        operation.OperationId.Should().NotContain("#");
        operation.OperationId.Should().NotContain(" ");
        operation.OperationId.Length.Should().BeLessThan(80);
    }

    [Fact]
    public async Task RequestRollbackAsync_OnSubmittedOperation_UpdatesAuditFields()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new ImmediateDeployBackend();
        var service = CreateService(store, backend);
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Submitted,
            requestedBy: "alice",
            reason: "Ship it");

        await store.TryCreateAsync(operation);

        var rolledBack = await service.RequestRollbackAsync(
            operation.OperationId,
            requestedBy: "bob",
            reason: "Smoke test failed");

        rolledBack.Should().NotBeNull();
        rolledBack!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        rolledBack.Audit.RequestedBy.Should().Be("bob");
        rolledBack.Audit.Reason.Should().Be("Smoke test failed");
    }

    [Fact]
    public async Task Reconciler_TransitionsSubmittedOperation_ToSucceeded()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.Submitted);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var reconciling = await store.GetAsync(operation.OperationId);
        reconciling!.Status.Should().Be(WorkflowOperationStatus.Reconciling);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var succeeded = await store.GetAsync(operation.OperationId);
        succeeded!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        succeeded.CompletedAt.Should().NotBeNull();
        succeeded.Deploy!.CurrentRevision.Should().Be(succeeded.Deploy.DesiredRevision);
    }

    [Fact]
    public async Task Reconciler_TransitionsRollbackRequestedOperation_ToRolledBack()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperationRecord(status: WorkflowOperationStatus.RollbackRequested);
        await store.TryCreateAsync(operation);

        var reconciler = CreateReconciler(store, backend);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var rolledBack = await store.GetAsync(operation.OperationId);
        rolledBack!.Status.Should().Be(WorkflowOperationStatus.RolledBack);
        rolledBack.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Reconciler_WaitsForTelemetryConfirmation_BeforeSettlingSucceededOperation()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["namespace"] = "honua",
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20",
                ["telemetry.latency_p95.query"] = "honua_canary_latency_p95",
                ["telemetry.latency_p95.threshold_ms"] = "2000"
            });
        await store.TryCreateAsync(operation);

        var evaluator = CreateTelemetryEvaluator("""
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.01"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"150"]}]}}
            """);
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        updated.CurrentPhase.Should().Contain("Telemetry gate passed");
    }

    [Fact]
    public async Task Reconciler_RequestsRollback_WhenTelemetryBackendDetectsBreach()
    {
        var store = new TestWorkflowOperationStore();
        var backend = new KubernetesGitOpsDeployBackend(NullLogger<KubernetesGitOpsDeployBackend>.Instance);
        var operation = CreateOperationRecord(
            status: WorkflowOperationStatus.Reconciling,
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            parameters: new Dictionary<string, string>
            {
                ["namespace"] = "honua",
                ["telemetry.connection"] = "prod-prom",
                ["telemetry.error_rate.query"] = "honua_canary_error_rate",
                ["telemetry.error_rate.threshold"] = "0.05",
                ["telemetry.sample_count.query"] = "honua_canary_sample_count",
                ["telemetry.sample_count.minimum"] = "20",
                ["telemetry.latency_p95.query"] = "honua_canary_latency_p95",
                ["telemetry.latency_p95.threshold_ms"] = "2000"
            });
        await store.TryCreateAsync(operation);

        var evaluator = CreateTelemetryEvaluator("""
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"0.25"]}]}}
            """,
            """
            {"status":"success","data":{"resultType":"vector","result":[{"metric":{},"value":[1710000000,"150"]}]}}
            """);
        var reconciler = CreateReconciler(store, backend, evaluator);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var rollbackRequested = await store.GetAsync(operation.OperationId);

        rollbackRequested.Should().NotBeNull();
        rollbackRequested!.Status.Should().Be(WorkflowOperationStatus.RollbackRequested);
        rollbackRequested.CurrentPhase.Should().Contain("Automatic rollback requested");
    }

    private static DeployWorkflowService CreateService(
        TestWorkflowOperationStore store,
        IDeployBackend backend)
        => new(
            new TestDeployTargetRegistry(),
            [store],
            [backend]);

    private static DeployWorkflowReconciler CreateReconciler(
        TestWorkflowOperationStore store,
        IDeployBackend backend,
        IDeployTelemetrySignalEvaluator? telemetryEvaluator = null)
        => new(
            store,
            new TestDeployTargetRegistry(),
            [backend],
            telemetryEvaluator ?? new StubDeployTelemetrySignalEvaluator(null),
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperationRecord(
        WorkflowOperationStatus status,
        string requestedBy = "alice",
        string reason = "Ship it",
        DateTimeOffset? createdAt = null,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var now = createdAt ?? DateTimeOffset.UtcNow;
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            CurrentPhase = "Submitted to deploy backend.",
            Audit = new OperationAuditInfo
            {
                RequestedBy = requestedBy,
                Reason = reason,
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-api",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-api",
                TargetKind = DeployTargetKind.Kubernetes,
                Backend = "honua-gitops-kubernetes",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                RuntimeProfile = "dotnet-api",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                Parameters = parameters ?? new Dictionary<string, string>
                {
                    ["namespace"] = "honua"
                }
            }
        };
    }

    private static PrometheusDeployTelemetrySignalEvaluator CreateTelemetryEvaluator(params string[] responses)
    {
        var responseQueue = new ConcurrentQueue<string>(responses);
        var optionsMonitor = new TestControlPlaneOptionsMonitor(new ControlPlaneOptions
        {
            TelemetryConnections =
            [
                new DeployTelemetryConnectionOptions
                {
                    ConnectionId = "prod-prom",
                    Provider = "prometheus",
                    BaseUrl = "https://prometheus.example",
                    TimeoutSeconds = 2
                }
            ]
        });

        return new PrometheusDeployTelemetrySignalEvaluator(
            optionsMonitor,
            new StubHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(_ =>
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        responseQueue.TryDequeue(out var responseJson)
                            ? responseJson
                            : """{"status":"success","data":{"resultType":"vector","result":[]}}""",
                        Encoding.UTF8,
                        "application/json")
                }))),
            NullLogger<PrometheusDeployTelemetrySignalEvaluator>.Instance);
    }

    private sealed class TestDeployTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-api",
            TargetKind = DeployTargetKind.Kubernetes,
            Backend = "honua-gitops-kubernetes",
            Environment = "production",
            TargetName = "honua-server",
            ArtifactReference = "ghcr.io/honua/server",
            RuntimeProfile = "dotnet-api",
            Parameters = new Dictionary<string, string>
            {
                ["namespace"] = "honua"
            }
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

    private sealed class TestControlPlaneOptionsMonitor(ControlPlaneOptions currentValue) : IOptionsMonitor<ControlPlaneOptions>
    {
        public ControlPlaneOptions CurrentValue => currentValue;

        public ControlPlaneOptions Get(string? name) => currentValue;

        public IDisposable? OnChange(Action<ControlPlaneOptions, string?> listener) => null;
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class TestWorkflowOperationStore : IWorkflowOperationStore
    {
        private readonly ConcurrentDictionary<string, WorkflowOperationRecord> _operations = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

        public bool AllowCreate { get; set; } = true;

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
        {
            if (!AllowCreate)
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(_operations.TryAdd(operation.OperationId, operation));
        }

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult(_operations.TryGetValue(operationId, out var operation) ? operation : null);

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

    private sealed class ImmediateDeployBackend : IDeployBackend
    {
        private int _startCount;

        public int StartCount => _startCount;

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
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            return Task.FromResult(new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"test-backend:{operation.OperationId}",
                Message = "Submitted"
            });
        }

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = operation.Status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = operation.CurrentPhase
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }

    private sealed class BlockingDeployBackend : IDeployBackend
    {
        private readonly TaskCompletionSource<bool> _startEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _allowStart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _startCount;

        public int StartCount => _startCount;

        public Task StartEntered => _startEntered.Task;

        public string BackendName => "honua-gitops-kubernetes";

        public DeployTargetKind TargetKind => DeployTargetKind.Kubernetes;

        public void AllowStart() => _allowStart.TrySetResult(true);

        public Task<DeployBackendCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployBackendCapabilities
            {
                SupportsRollback = true,
                SupportsProgressPolling = true,
                SupportsRevisionPinning = true
            });

        public Task<DeployPlan> PlanAsync(DeployOperationSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployPlan
            {
                IsReadyToSubmit = true
            });

        public async Task<DeploySubmissionResult> StartAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _startCount);
            _startEntered.TrySetResult(true);
            await _allowStart.Task.WaitAsync(cancellationToken);
            return new DeploySubmissionResult
            {
                Status = WorkflowOperationStatus.Submitted,
                ProviderOperationId = $"test-backend:{operation.OperationId}",
                Message = "Submitted"
            };
        }

        public Task<DeployObservation> ObserveAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = operation.Status,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.DesiredRevision,
                Message = operation.CurrentPhase
            });

        public Task<DeployObservation> RollbackAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                ObservedRevision = operation.Deploy?.CurrentRevision,
                Message = "Rollback requested"
            });
    }
}
