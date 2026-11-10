// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Workers whose clocks are an hour ahead of and an hour behind the database write to the store.
/// Claims, retry delays, heartbeat expiry and leases must come out as if their clocks were right.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobStoreClockTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _tolerance = TimeSpan.FromSeconds(30);

    [IntegrationTheory]
    [InlineData(60)]
    [InlineData(-60)]
    public async Task ClaimLeaseAndHeartbeatExpiry_UnderSkewedNodeClocks_FollowDatabaseTime(int nodeClockOffsetMinutes)
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobStoreClockTests));
        var skew = TimeSpan.FromMinutes(nodeClockOffsetMinutes);
        DateTimeOffset NodeNow() => DateTimeOffset.UtcNow + skew;

        var job = CreateQueuedJob($"skewed-{Guid.NewGuid():N}", NodeNow());
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(job.OperationId);
        (await harness.Queue.TryClaimAsync("skewed-worker")).Should().Be(job.OperationId);

        // A heartbeat stamped with the node's clock is stored as the database's time.
        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        (await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Running,
            LastHeartbeatAt = NodeNow(),
            UpdatedAt = NodeNow()
        })).Should().BeTrue();
        var running = (await harness.JobStore.GetAsync(job.OperationId))!;
        var databaseNow = await harness.JobStore.GetUtcNowAsync();
        running.LastHeartbeatAt.Should().BeCloseTo(databaseNow, _tolerance);
        running.ClaimedAt.Should().BeCloseTo(databaseNow, _tolerance);
        running.UpdatedAt.Should().BeCloseTo(databaseNow, _tolerance);

        // A live heartbeat is not expired by a reconciler, whichever way the worker's clock is off.
        await harness.RunReconciliationSweepAsync(CancellationToken.None);
        (await harness.JobStore.GetAsync(job.OperationId))!.Status.Should().Be(ExecutionJobStatus.Running);

        // A heartbeat that is old on the database clock is expired, and the retry delay the
        // reconciler asks for is honoured on the database clock.
        await harness.AgeClaimAsync(job.OperationId, TimeSpan.FromMinutes(5));
        await harness.RunReconciliationSweepAsync(CancellationToken.None);
        var requeued = (await harness.JobStore.GetAsync(job.OperationId))!;
        requeued.Status.Should().Be(ExecutionJobStatus.Queued);
        requeued.NextRetryAt.Should().BeCloseTo(databaseNow.AddSeconds(3), _tolerance);
        (await harness.Queue.TryClaimAsync("other-worker")).Should().BeNull("the three-second retry delay has not elapsed");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        (await harness.Queue.TryClaimAsync("other-worker")).Should().Be(job.OperationId);

        // A worker with a skewed clock hands the job back with a delay computed on its own clock.
        var reclaimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        var writerNow = NodeNow();
        (await harness.JobStore.TrySetAsync(reclaimed with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = writerNow,
            NextRetryAt = writerNow.AddSeconds(3)
        })).Should().BeTrue();
        (await harness.Queue.TryClaimAsync("skewed-worker")).Should().BeNull("the delay is three seconds, not the clock offset");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        (await harness.Queue.TryClaimAsync("skewed-worker")).Should().Be(job.OperationId);

        // A lease lasts its duration on the database clock.
        var leaseKey = $"partition:{Guid.NewGuid():N}";
        (await harness.JobStore.TryAcquireLeaseAsync(leaseKey, "owner-a", TimeSpan.FromSeconds(2))).Should().BeTrue();
        (await harness.JobStore.TryAcquireLeaseAsync(leaseKey, "owner-b", TimeSpan.FromSeconds(2))).Should().BeFalse();
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        (await harness.JobStore.RenewLeaseAsync(leaseKey, "owner-a", TimeSpan.FromSeconds(2))).Should().BeFalse("an expired lease cannot be renewed");
        (await harness.JobStore.TryAcquireLeaseAsync(leaseKey, "owner-b", TimeSpan.FromSeconds(2))).Should().BeTrue();
    }

    private static ExecutionJobRecord CreateQueuedJob(string operationId, DateTimeOffset nodeNow)
        => new()
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            CreatedAt = nodeNow,
            UpdatedAt = nodeNow,
            RetryPolicy = new JobRetryPolicy
            {
                MaxAttempts = 5,
                Strategy = BackoffStrategy.Fixed,
                BaseDelay = TimeSpan.FromSeconds(3),
                MaxDelay = TimeSpan.FromSeconds(3)
            },
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = LocalBatchComputeBackend.BackendId,
                WorkloadName = "postgres-job-store-clock"
            }
        };
}
