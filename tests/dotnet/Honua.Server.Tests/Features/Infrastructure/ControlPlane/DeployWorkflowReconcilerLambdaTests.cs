// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Reconciler-level coverage for the Lambda alias rollout backend wiring,
/// including telemetry-gated promotion and rollback paths.
/// </summary>
public sealed class DeployWorkflowReconcilerLambdaTests
{
    [Fact]
    public async Task ReconcileAsync_WhenTelemetryRollbackRecommended_CallsRollbackOnLambdaBackend()
    {
        var aliasClient = new RecordingAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda:live",
                FunctionVersion = "41",
                AdditionalVersionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["42"] = 0.10d
                }
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(
            currentRevision: "41",
            desiredRevision: "42",
            status: WorkflowOperationStatus.Reconciling);
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
        updated.CurrentPhase.Should().Contain("Automatic rollback requested");
        updated.ErrorMessage.Should().Contain("telemetry detected canary degradation");
        aliasClient.UpdateCalls.Should().Contain(call => call.FunctionVersion == "41" && call.HasNoWeights);
    }

    [Fact]
    public async Task ReconcileAsync_WhenTelemetryWarmupPending_OperationRemainsReconciling()
    {
        var aliasClient = new RecordingAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda:live",
                FunctionVersion = "41",
                AdditionalVersionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["42"] = 0.10d
                }
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(
            currentRevision: "41",
            desiredRevision: "42",
            status: WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            WaitForMoreTelemetry = true,
            Message = "Waiting for telemetry warmup to complete before settling deploy."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Reconciling);
        updated.CurrentPhase.Should().Contain("Waiting for telemetry");
        aliasClient.UpdateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconcileAsync_WhenPromotionRecommendedAndTelemetryClear_CallsPromoteOnLambdaBackend()
    {
        var aliasClient = new RecordingAwsLambdaAliasClient
        {
            CurrentState = new AwsLambdaAliasState
            {
                AliasName = "live",
                AliasArn = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda:live",
                FunctionVersion = "41",
                AdditionalVersionWeights = new Dictionary<string, double>(StringComparer.Ordinal)
                {
                    ["42"] = 0.10d
                }
            }
        };
        var backend = new AwsLambdaGitOpsDeployBackend(aliasClient, NullLogger<AwsLambdaGitOpsDeployBackend>.Instance);
        var store = new InMemoryWorkflowOperationStore();
        var operation = CreateOperation(
            currentRevision: "41",
            desiredRevision: "42",
            status: WorkflowOperationStatus.Reconciling);
        await store.TryCreateAsync(operation);

        var telemetry = new StubDeployTelemetrySignalEvaluator(new DeployTelemetryDecision
        {
            Message = "Telemetry gate passed."
        });
        var reconciler = CreateReconciler(store, backend, telemetry);

        await reconciler.ReconcileWorkflowOperationAsync(operation.OperationId);
        var updated = await store.GetAsync(operation.OperationId);

        updated.Should().NotBeNull();
        updated!.Status.Should().Be(WorkflowOperationStatus.Succeeded);
        updated.CompletedAt.Should().NotBeNull();
        updated.ObservedState.Should().Be("42");
        updated.Deploy!.CurrentRevision.Should().Be("41");
        aliasClient.UpdateCalls.Should().HaveCountGreaterOrEqualTo(1);
        aliasClient.UpdateCalls.Last().FunctionVersion.Should().Be("42");
        aliasClient.UpdateCalls.Last().HasNoWeights.Should().BeTrue();
    }

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

    private static WorkflowOperationRecord CreateOperation(
        string? currentRevision,
        string desiredRevision,
        WorkflowOperationStatus status)
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
                Reason = "Lambda canary",
                IdempotencyKey = Guid.NewGuid().ToString("N")
            },
            Concurrency = new OperationConcurrencyPolicy
            {
                PartitionKey = "production:prod-lambda",
                RequiresExclusiveLease = true
            },
            Deploy = new DeployOperationSpec
            {
                TargetId = "prod-lambda",
                TargetKind = DeployTargetKind.AwsLambda,
                Backend = "honua-gitops-aws-lambda",
                Environment = "production",
                TargetName = "honua-prod-lambda",
                ArtifactReference = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:sha-42",
                CurrentRevision = currentRevision,
                DesiredRevision = desiredRevision,
                RequiresOutOfBandMigrations = true,
                Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["lambda.alias_name"] = "live",
                    ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda",
                    ["telemetry.connection"] = "prod-prom",
                    ["lambda.canary_weight_percentage"] = "10"
                }
            }
        };
    }

    private sealed class SingleTargetRegistry : IDeployTargetRegistry
    {
        private static readonly DeployTargetDefinition Target = new()
        {
            TargetId = "prod-lambda",
            TargetKind = DeployTargetKind.AwsLambda,
            Backend = "honua-gitops-aws-lambda",
            Environment = "production",
            TargetName = "honua-prod-lambda",
            ArtifactReference = "123456789012.dkr.ecr.us-east-1.amazonaws.com/honua:sha-42",
            Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["lambda.alias_name"] = "live",
                ["target.resource_id"] = "arn:aws:lambda:us-east-1:123456789012:function:honua-prod-lambda"
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

    private sealed class RecordingAwsLambdaAliasClient : IAwsLambdaAliasClient
    {
        public AwsLambdaAliasState CurrentState { get; set; } = new()
        {
            AliasName = "live",
            FunctionVersion = "1"
        };

        public List<UpdateAliasCall> UpdateCalls { get; } = [];

        public Task<AwsLambdaAliasState> GetAliasAsync(
            string functionName,
            string aliasName,
            string? region,
            CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentState);

        public Task<AwsLambdaAliasState> UpdateAliasAsync(
            string functionName,
            string aliasName,
            string functionVersion,
            IReadOnlyDictionary<string, double>? additionalVersionWeights,
            string? region,
            CancellationToken cancellationToken = default)
        {
            var weights = additionalVersionWeights is { Count: > 0 }
                ? new Dictionary<string, double>(additionalVersionWeights, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
            UpdateCalls.Add(new UpdateAliasCall(functionVersion, weights));

            CurrentState = CurrentState with
            {
                AliasName = aliasName,
                FunctionVersion = functionVersion,
                AdditionalVersionWeights = weights
            };
            return Task.FromResult(CurrentState);
        }

        public sealed record UpdateAliasCall(string FunctionVersion, IReadOnlyDictionary<string, double> AdditionalVersionWeights)
        {
            public bool HasNoWeights => AdditionalVersionWeights.Count == 0;
        }
    }
}
