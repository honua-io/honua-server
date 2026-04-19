// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed class ExecutionJobReconcilerTests
{
    [Fact]
    public async Task ReconcileAsync_MarksJobFailedWhenNoBackendRegistered()
    {
        var store = new InMemoryExecutionJobStore();
        var job = AwsBatchComputeBackendTests.CreateJob();
        await store.TryCreateAsync(job);
        var reconciler = new ExecutionJobReconciler(
            store,
            Array.Empty<IBatchComputeBackend>(),
            NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        var persisted = await store.GetAsync(job.OperationId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ExecutionJobStatus.Failed);
        persisted.CompletedAt.Should().NotBeNull();
        persisted.ErrorMessage.Should().Contain("honua-aws-batch");
    }

    [Fact]
    public async Task ReconcileAsync_SubmitsJobWhenProviderIdMissing()
    {
        var store = new InMemoryExecutionJobStore();
        var job = AwsBatchComputeBackendTests.CreateJob();
        await store.TryCreateAsync(job);
        var stubClient = new StubAwsBatchJobClient
        {
            NextSubmitResult = new AwsBatchSubmitResult
            {
                JobId = "aws-job-123",
                JobArn = "arn:aws:batch:us-west-2:1:job/aws-job-123",
                JobName = "honua-job"
            }
        };
        var backend = new AwsBatchComputeBackend(stubClient, NullLogger<AwsBatchComputeBackend>.Instance);
        var reconciler = new ExecutionJobReconciler(store, new[] { (IBatchComputeBackend)backend }, NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        var persisted = await store.GetAsync(job.OperationId);
        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(ExecutionJobStatus.Queued);
        persisted.ProviderOperationId.Should().Be("aws-job-123");
        stubClient.LastSubmission.Should().NotBeNull();
    }

    [Fact]
    public async Task ReconcileAsync_ObservesAndTransitionsTerminalOnSucceeded()
    {
        var store = new InMemoryExecutionJobStore();
        var job = AwsBatchComputeBackendTests.CreateJob(providerOperationId: "aws-job-123", status: ExecutionJobStatus.Running);
        await store.TryCreateAsync(job);
        var stubClient = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-123",
                Status = "SUCCEEDED"
            }
        };
        var backend = new AwsBatchComputeBackend(stubClient, NullLogger<AwsBatchComputeBackend>.Instance);
        var reconciler = new ExecutionJobReconciler(store, new[] { (IBatchComputeBackend)backend }, NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        var persisted = await store.GetAsync(job.OperationId);
        persisted!.Status.Should().Be(ExecutionJobStatus.Succeeded);
        persisted.CompletedAt.Should().NotBeNull();
        stubClient.DescribeCallCount.Should().Be(1);
    }

    [Fact]
    public async Task ReconcileAsync_SkipsTerminalJobs()
    {
        var store = new InMemoryExecutionJobStore();
        var job = AwsBatchComputeBackendTests.CreateJob(providerOperationId: "aws-job-123", status: ExecutionJobStatus.Succeeded) with
        {
            CompletedAt = DateTimeOffset.UtcNow
        };
        await store.TryCreateAsync(job);
        var stubClient = new StubAwsBatchJobClient();
        var backend = new AwsBatchComputeBackend(stubClient, NullLogger<AwsBatchComputeBackend>.Instance);
        var reconciler = new ExecutionJobReconciler(store, new[] { (IBatchComputeBackend)backend }, NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        stubClient.DescribeCallCount.Should().Be(0);
        var persisted = await store.GetAsync(job.OperationId);
        persisted!.Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [Fact]
    public async Task ReconcileAsync_ReturnsSilentlyWhenLeaseIsUnavailable()
    {
        var store = new InMemoryExecutionJobStore { LockOut = true };
        var job = AwsBatchComputeBackendTests.CreateJob();
        await store.TryCreateAsync(job);
        var stubClient = new StubAwsBatchJobClient();
        var backend = new AwsBatchComputeBackend(stubClient, NullLogger<AwsBatchComputeBackend>.Instance);
        var reconciler = new ExecutionJobReconciler(store, new[] { (IBatchComputeBackend)backend }, NullLogger<ExecutionJobReconciler>.Instance);

        await reconciler.ReconcileAsync(job.OperationId);

        stubClient.LastSubmission.Should().BeNull();
    }

    [Fact]
    public async Task CompositeReconciler_RoutesWorkflowAndExecutionJobs()
    {
        var store = new InMemoryExecutionJobStore();
        var job = AwsBatchComputeBackendTests.CreateJob(providerOperationId: "aws-job-456", status: ExecutionJobStatus.Running);
        await store.TryCreateAsync(job);
        var stubClient = new StubAwsBatchJobClient
        {
            NextDescribeResult = new AwsBatchJobState
            {
                JobId = "aws-job-456",
                Status = "RUNNING"
            }
        };
        var backend = new AwsBatchComputeBackend(stubClient, NullLogger<AwsBatchComputeBackend>.Instance);
        var executionReconciler = new ExecutionJobReconciler(store, new[] { (IBatchComputeBackend)backend }, NullLogger<ExecutionJobReconciler>.Instance);

        // DeployWorkflowReconciler can be constructed with empty registries; it will short-circuit on a missing operation.
        var workflowReconciler = new DeployWorkflowReconciler(
            new NoopWorkflowOperationStore(),
            new NoopDeployTargetRegistry(),
            Array.Empty<IDeployBackend>(),
            new NoopTelemetryEvaluator(),
            NullLogger<DeployWorkflowReconciler>.Instance);

        var composite = new CompositeOperationReconciler(workflowReconciler, executionReconciler);

        await ((IOperationReconciler)composite).ReconcileExecutionJobAsync(job.OperationId);
        await ((IOperationReconciler)composite).ReconcileWorkflowOperationAsync("unknown-workflow");

        stubClient.DescribeCallCount.Should().Be(1);
    }

    private sealed class NoopWorkflowOperationStore : IWorkflowOperationStore
    {
        public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<bool> TryCreateAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<WorkflowOperationRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
            => Task.FromResult<WorkflowOperationRecord?>(null);

        public Task SetAsync(WorkflowOperationRecord operation, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<WorkflowOperationRecord>> ListActiveAsync(WorkflowOperationKind? kind = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkflowOperationRecord>>(Array.Empty<WorkflowOperationRecord>());
    }

    private sealed class NoopDeployTargetRegistry : IDeployTargetRegistry
    {
        public Task<IReadOnlyList<DeployTargetDefinition>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DeployTargetDefinition>>(Array.Empty<DeployTargetDefinition>());

        public Task<DeployTargetDefinition?> GetAsync(string targetId, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTargetDefinition?>(null);
    }

    private sealed class NoopTelemetryEvaluator : IDeployTelemetrySignalEvaluator
    {
        public Task<DeployTelemetryDecision?> EvaluateAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken = default)
            => Task.FromResult<DeployTelemetryDecision?>(null);
    }
}

internal sealed class InMemoryExecutionJobStore : IExecutionJobStore
{
    private readonly ConcurrentDictionary<string, ExecutionJobRecord> _records = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _leases = new(StringComparer.Ordinal);

    public bool LockOut { get; set; }

    public Task<bool> TryAcquireLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (LockOut)
        {
            return Task.FromResult(false);
        }

        var acquired = _leases.TryAdd(operationId, ownerId);
        return Task.FromResult(acquired);
    }

    public Task<bool> RenewLeaseAsync(string operationId, string ownerId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        if (_leases.TryGetValue(operationId, out var currentOwner) && currentOwner == ownerId)
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    public Task ReleaseLeaseAsync(string operationId, string ownerId, CancellationToken cancellationToken = default)
    {
        _leases.TryRemove(new KeyValuePair<string, string>(operationId, ownerId));
        return Task.CompletedTask;
    }

    public Task<bool> TryCreateAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.TryAdd(job.OperationId, job));

    public Task<ExecutionJobRecord?> GetAsync(string operationId, CancellationToken cancellationToken = default)
        => Task.FromResult(_records.TryGetValue(operationId, out var job) ? job : null);

    public Task SetAsync(ExecutionJobRecord job, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        _records[job.OperationId] = job;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExecutionJobRecord>> ListActiveAsync(ExecutionJobKind? kind = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ExecutionJobRecord> active = _records.Values
            .Where(job => job.Status is not (ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled))
            .Where(job => !kind.HasValue || job.Spec.Kind == kind.Value)
            .ToArray();
        return Task.FromResult(active);
    }
}
