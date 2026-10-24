// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciler-level proof for honua-server#6474: a CloudWatch-gated canary whose target group served
/// no 5XX responses publishes no <c>HTTPCode_Target_5XX_Count</c> datapoint. With an error-count
/// numerator and a floor-meeting sample count the reconciler must promote it, and it must keep
/// holding when the sample count itself is missing.
/// </summary>
public sealed class DeployWorkflowReconcilerCloudWatchZeroErrorTests
{
    private const string SampleExpression = "SUM(SEARCH('{AWS/ApplicationELB,TargetGroup} RequestCount', 'Sum', 300))";
    private const string ErrorCountExpression = "SUM(SEARCH('{AWS/ApplicationELB,TargetGroup} HTTPCode_Target_5XX_Count', 'Sum', 300))";
    private const string LatencyExpression = "p95_target_response_time_ms";

    [UnitTest]
    public async Task Reconcile_HealthyZeroErrorCanary_PromotesOnTheCloudWatchGate()
    {
        var client = new ScriptedCloudWatchMetricClient
        {
            [SampleExpression] = Datapoint(240),
            // A SEARCH that matches no metric: no 5XX was ever published for the candidate.
            [ErrorCountExpression] = new GetMetricDataResponse { MetricDataResults = [] },
            [LatencyExpression] = Datapoint(85)
        };
        var (store, backend, operation) = await ArrangeAsync();

        await CreateReconciler(store, backend, client).ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(1, "a floor-meeting window with no 5XX series is a healthy, zero-error canary");
        backend.RollbackCalls.Should().Be(0);
        client.Requested.Should().Contain(ErrorCountExpression);
        updated!.Deploy!.Protection.Should().NotBeNull("promotion opens the post-activation observation window");
    }

    [UnitTest]
    public async Task Reconcile_ZeroErrorCanaryWithoutSampleCount_HoldsAndDoesNotPromote()
    {
        var client = new ScriptedCloudWatchMetricClient
        {
            [SampleExpression] = new GetMetricDataResponse { MetricDataResults = [] },
            [ErrorCountExpression] = new GetMetricDataResponse { MetricDataResults = [] },
            [LatencyExpression] = Datapoint(85)
        };
        var (store, backend, operation) = await ArrangeAsync();

        await CreateReconciler(store, backend, client).ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        backend.PromoteCalls.Should().Be(0, "no sample count is no evidence that the candidate served traffic");
        backend.RollbackCalls.Should().Be(0, "missing evidence is held within the grace window, not rolled back at once");
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        updated.CurrentPhase.Should().Contain("no sample-count signal");
        client.Requested.Should().NotContain(ErrorCountExpression, "the numerator is not read until the floor is met");
    }

    private static GetMetricDataResponse Datapoint(double value)
        => new()
        {
            MetricDataResults = [new MetricDataResult { Id = "honua_deploy_signal", StatusCode = StatusCode.Complete, Values = [value] }]
        };

    private static async Task<(InMemoryWorkflowOperationStore Store, RecordingDeployBackend Backend, WorkflowOperationRecord Operation)> ArrangeAsync()
    {
        var store = new InMemoryWorkflowOperationStore();
        var backend = new RecordingDeployBackend(
            DeployTargetKind.AwsEcs,
            "honua-aws-ecs-alb",
            observe: new DeployObservation
            {
                Status = WorkflowOperationStatus.Reconciling,
                PromotionRecommended = true,
                Message = "Canary target group healthy."
            });
        var operation = CreateOperation();
        await store.TryCreateAsync(operation);
        return (store, backend, operation);
    }

    private static DeployWorkflowReconciler CreateReconciler(
        IWorkflowOperationStore store,
        RecordingDeployBackend backend,
        ICloudWatchMetricClient client)
        => new(
            store,
            new SingleTargetRegistry(backend.TargetKind, backend.BackendName),
            [backend],
            new DeployTelemetrySignalEvaluator(
                new OptionsMonitorStub(new ControlPlaneOptions
                {
                    TelemetryConnections =
                    [
                        new DeployTelemetryConnectionOptions
                        {
                            ConnectionId = "prod-cw",
                            Provider = "cloudwatch",
                            BaseUrl = "https://monitoring.us-east-1.amazonaws.com",
                            TimeoutSeconds = 2
                        }
                    ]
                }),
                [new CloudWatchDeployTelemetryProviderEvaluator(client)],
                NullLogger<DeployTelemetrySignalEvaluator>.Instance),
            NullLogger<DeployWorkflowReconciler>.Instance);

    private static WorkflowOperationRecord CreateOperation()
    {
        var exposedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        return new WorkflowOperationRecord
        {
            OperationId = $"deploy-{Guid.NewGuid():N}",
            Kind = WorkflowOperationKind.Deploy,
            Status = WorkflowOperationStatus.Reconciling,
            CreatedAt = exposedAt,
            UpdatedAt = exposedAt,
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
                TargetKind = DeployTargetKind.AwsEcs,
                Backend = "honua-aws-ecs-alb",
                Environment = "production",
                TargetName = "honua-server",
                ArtifactReference = "ghcr.io/honua/server",
                CurrentRevision = "sha256:old",
                DesiredRevision = "sha256:new",
                TrafficExposedAt = exposedAt,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["telemetry.connection"] = "prod-cw",
                    ["telemetry.error_count.query"] = ErrorCountExpression,
                    ["telemetry.error_rate.threshold"] = "0.05",
                    ["telemetry.latency_p95.query"] = LatencyExpression,
                    ["telemetry.latency_p95.threshold_ms"] = "2000",
                    ["telemetry.sample_count.query"] = SampleExpression,
                    ["telemetry.sample_count.minimum"] = "20"
                }
            }
        };
    }

    /// <summary>Answers each expression with a raw response read through the production reader.</summary>
    private sealed class ScriptedCloudWatchMetricClient : Dictionary<string, GetMetricDataResponse>, ICloudWatchMetricClient
    {
        public ScriptedCloudWatchMetricClient()
            : base(StringComparer.Ordinal)
        {
        }

        public ConcurrentQueue<string> Requested { get; } = new();

        public Task<CloudWatchSignalReading> GetExpressionReadingAsync(
            string? region,
            string expression,
            DateTime startUtc,
            DateTime endUtc,
            int periodSeconds,
            string? serviceUrl = null,
            CancellationToken cancellationToken = default)
        {
            Requested.Enqueue(expression);
            return Task.FromResult(TryGetValue(expression, out var response)
                ? AwsSdkCloudWatchMetricClient.SelectSignalReading(response)
                : CloudWatchSignalReading.Unavailable);
        }
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

        public int RollbackCalls { get; private set; }

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
        {
            RollbackCalls++;
            return Task.FromResult(new DeployObservation
            {
                Status = WorkflowOperationStatus.RollbackRequested,
                ProviderOperationId = operation.ProviderOperationId,
                Message = "Rollback requested"
            });
        }
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
