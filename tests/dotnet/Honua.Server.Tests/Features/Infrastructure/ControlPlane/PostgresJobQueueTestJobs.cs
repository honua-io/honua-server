// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal sealed record QueuedJobSpec
{
    public ExecutionJobKind Kind { get; init; } = ExecutionJobKind.Geoprocessing;

    public string? RuntimeProfile { get; init; }

    public string Backend { get; init; } = LocalBatchComputeBackend.BackendId;
}

internal static class PostgresJobQueueTestJobs
{
    public static async Task<ExecutionJobRecord> CreateEnqueuedJobAsync(
        PostgresJobSubstrateHarness harness,
        string name,
        OperationPriority priority = OperationPriority.Normal,
        QueuedJobSpec? spec = null)
    {
        var job = CreateQueuedJob(name, priority, spec);
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(job.OperationId, priority);
        return job;
    }

    public static ExecutionJobRecord CreateQueuedJob(
        string name,
        OperationPriority priority = OperationPriority.Normal,
        QueuedJobSpec? spec = null)
    {
        spec ??= new QueuedJobSpec();
        var created = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"{name}-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            Priority = priority,
            CreatedAt = created,
            UpdatedAt = created,
            Spec = new ExecutionJobSpec
            {
                Kind = spec.Kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = spec.Backend,
                WorkloadName = "postgres-job-queue",
                RuntimeProfile = spec.RuntimeProfile
            }
        };
    }
}
