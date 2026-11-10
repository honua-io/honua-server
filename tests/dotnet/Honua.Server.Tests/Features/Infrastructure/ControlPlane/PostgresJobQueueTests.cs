// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// The job queue contract against PostgreSQL: which jobs a claim may take, in which order, and
/// that a job in the queue is never stranded.
/// </summary>
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobQueueTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly IReadOnlySet<string> _nativeOnly = new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

    [IntegrationTest]
    public async Task TryClaimAsync_ClaimedJob_IsProvisioningWithOneAttemptOwnedByTheWorker()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "claim-shape");

        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(job.OperationId);

        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        claimed.Status.Should().Be(ExecutionJobStatus.Provisioning);
        claimed.ClaimedBy.Should().Be("worker-a");
        claimed.AttemptCount.Should().Be(1);
        claimed.Version.Should().Be(2);
        claimed.CurrentPhase.Should().Be("Claimed");
        claimed.NextRetryAt.Should().BeNull();
        var databaseNow = await harness.JobStore.GetUtcNowAsync();
        claimed.ClaimedAt.Should().BeCloseTo(databaseNow, TimeSpan.FromSeconds(30));
        claimed.LastHeartbeatAt.Should().Be(claimed.ClaimedAt);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_QueuedJobThatWasNeverEnqueued_IsNotClaimed()
    {
        await using var harness = await CreateHarnessAsync();
        var awaitingRemoteSubmission = CreateQueuedJob("never-enqueued");
        (await harness.JobStore.TryCreateAsync(awaitingRemoteSubmission)).Should().BeTrue();

        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
        (await harness.JobStore.GetAsync(awaitingRemoteSubmission.OperationId))!.Status.Should().Be(ExecutionJobStatus.Queued);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_OrdersByPriorityThenCreationTime()
    {
        await using var harness = await CreateHarnessAsync();
        var created = DateTimeOffset.UtcNow.AddMinutes(-10);
        var normalOld = await CreateEnqueuedJobAsync(harness, "normal-old", OperationPriority.Normal, created);
        var normalNew = await CreateEnqueuedJobAsync(harness, "normal-new", OperationPriority.Normal, created.AddMinutes(1));
        var low = await CreateEnqueuedJobAsync(harness, "low", OperationPriority.Low, created.AddMinutes(-5));
        var critical = await CreateEnqueuedJobAsync(harness, "critical", OperationPriority.Critical, created.AddMinutes(5));
        var high = await CreateEnqueuedJobAsync(harness, "high", OperationPriority.High, created.AddMinutes(4));

        var order = new List<string?>();
        for (var claim = 0; claim < 5; claim++)
        {
            order.Add(await harness.Queue.TryClaimAsync("worker-a"));
        }

        order.Should().Equal(critical.OperationId, high.OperationId, normalOld.OperationId, normalNew.OperationId, low.OperationId);
        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenAllJobsAreDelayed_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        for (var index = 0; index < 20; index++)
        {
            var delayed = await CreateEnqueuedJobAsync(harness, $"delayed-{index}");
            await harness.Queue.RequeueAsync(delayed.OperationId, visibleAfter: TimeSpan.FromMinutes(10));
        }

        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(20, "a delayed job is still in the queue");
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenManyDelayedJobsPrecedeAReadyJob_ClaimsTheReadyJob()
    {
        await using var harness = await CreateHarnessAsync();
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        for (var index = 0; index < 150; index++)
        {
            var delayed = await CreateEnqueuedJobAsync(harness, $"delayed-{index:D3}", createdAt: created.AddSeconds(index));
            await harness.Queue.RequeueAsync(delayed.OperationId, visibleAfter: TimeSpan.FromMinutes(10));
        }

        var ready = await CreateEnqueuedJobAsync(harness, "ready", createdAt: created.AddSeconds(1000));

        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(ready.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenManyJobsOfAnotherKindPrecedeAReadyJob_ClaimsTheReadyJob()
    {
        await using var harness = await CreateHarnessAsync();
        var created = DateTimeOffset.UtcNow.AddHours(-1);
        for (var index = 0; index < 150; index++)
        {
            await CreateEnqueuedJobAsync(harness, $"import-{index:D3}", createdAt: created.AddSeconds(index), kind: ExecutionJobKind.FileImport);
        }

        var ready = await CreateEnqueuedJobAsync(harness, "geoprocessing", createdAt: created.AddSeconds(1000));

        (await harness.Queue.TryClaimAsync("worker-a", new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing }))
            .Should().Be(ready.OperationId);
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(150);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenFirstCandidateIsLockedByAnotherClaim_ClaimsTheNextCandidate()
    {
        await using var harness = await CreateHarnessAsync();
        var created = DateTimeOffset.UtcNow.AddMinutes(-10);
        var contested = await CreateEnqueuedJobAsync(harness, "contested", createdAt: created);
        var next = await CreateEnqueuedJobAsync(harness, "next", createdAt: created.AddMinutes(1));

        await using var competitor = await harness.DataSource.OpenConnectionAsync();
        await using var competingClaim = await competitor.BeginTransactionAsync();
        await using (var lockRow = new NpgsqlCommand(
                         $"SELECT 1 FROM {harness.JobsTable} WHERE operation_id = @operation_id FOR UPDATE", competitor, competingClaim))
        {
            lockRow.Parameters.Add(new NpgsqlParameter("operation_id", contested.OperationId));
            await lockRow.ExecuteScalarAsync();
        }

        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(next.OperationId);
        (await harness.Queue.TryClaimAsync("worker-b")).Should().BeNull("the contested job belongs to the claim that holds its row");

        await competingClaim.RollbackAsync();
        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(contested.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_JobThatBecameTerminalWhileQueued_IsNotClaimedAndLeavesTheQueue()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "cancelled-while-queued");
        var queued = (await harness.JobStore.GetAsync(job.OperationId))!;
        (await harness.JobStore.TrySetAsync(queued with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        })).Should().BeTrue();

        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
        (await harness.IsInQueueAsync(job.OperationId)).Should().BeFalse();
        (await harness.JobStore.GetAsync(job.OperationId))!.AttemptCount.Should().Be(0);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_LeanWorker_DoesNotClaimNativeJob()
    {
        await using var harness = await CreateHarnessAsync();
        await CreateEnqueuedJobAsync(harness, "native", runtimeProfile: RuntimeProfiles.Native);

        (await harness.Queue.TryClaimAsync("lean-worker", acceptedRuntimeProfiles: null)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryClaimAsync_LeanWorker_WithExplicitManagedSet_DoesNotClaimNativeJob()
    {
        await using var harness = await CreateHarnessAsync();
        await CreateEnqueuedJobAsync(harness, "native", runtimeProfile: RuntimeProfiles.Native);

        (await harness.Queue.TryClaimAsync("lean-worker", acceptedRuntimeProfiles: RuntimeProfiles.DefaultAccepted)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryClaimAsync_LeanWorker_ClaimsManagedJob()
    {
        await using var harness = await CreateHarnessAsync();
        var managed = await CreateEnqueuedJobAsync(harness, "managed", runtimeProfile: RuntimeProfiles.Managed);

        (await harness.Queue.TryClaimAsync("lean-worker")).Should().Be(managed.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_LeanWorker_ClaimsNullProfileJobAsManaged()
    {
        await using var harness = await CreateHarnessAsync();
        var unprofiled = await CreateEnqueuedJobAsync(harness, "unprofiled", runtimeProfile: null);

        (await harness.Queue.TryClaimAsync("lean-worker")).Should().Be(unprofiled.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_NativeWorker_ClaimsNativeJob()
    {
        await using var harness = await CreateHarnessAsync();
        var native = await CreateEnqueuedJobAsync(harness, "native", runtimeProfile: RuntimeProfiles.Native);

        (await harness.Queue.TryClaimAsync("native-worker", acceptedRuntimeProfiles: _nativeOnly)).Should().Be(native.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_NativeWorker_DoesNotClaimManagedJob()
    {
        await using var harness = await CreateHarnessAsync();
        await CreateEnqueuedJobAsync(harness, "managed", runtimeProfile: RuntimeProfiles.Managed);
        await CreateEnqueuedJobAsync(harness, "unprofiled", runtimeProfile: null);

        (await harness.Queue.TryClaimAsync("native-worker", acceptedRuntimeProfiles: _nativeOnly)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task EnqueueAsync_Repeated_KeepsTheOriginalPositionAndPriority()
    {
        await using var harness = await CreateHarnessAsync();
        var created = DateTimeOffset.UtcNow.AddMinutes(-10);
        var first = await CreateEnqueuedJobAsync(harness, "first", OperationPriority.Normal, created);
        var second = await CreateEnqueuedJobAsync(harness, "second", OperationPriority.Normal, created.AddMinutes(1));

        await harness.Queue.EnqueueAsync(second.OperationId, OperationPriority.Critical);

        (await harness.Queue.GetQueueDepthAsync()).Should().Be(2);
        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(first.OperationId);
    }

    [IntegrationTest]
    public async Task RequeueAsync_WithDelay_HidesTheJobUntilTheDelayElapses()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "backoff");
        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(job.OperationId);
        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        (await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null
        })).Should().BeTrue();

        await harness.Queue.RequeueAsync(job.OperationId, visibleAfter: TimeSpan.FromSeconds(3));

        (await harness.Queue.TryClaimAsync("worker-b")).Should().BeNull("the retry delay has not elapsed on the database clock");
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(1);
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(job.OperationId);
        (await harness.JobStore.GetAsync(job.OperationId))!.AttemptCount.Should().Be(2);
    }

    [IntegrationTest]
    public async Task TrySetAsync_ClaimedJobReturnedToQueued_IsClaimableWithoutRequeue()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "requeue-lost");
        (await harness.Queue.TryClaimAsync("worker-that-dies")).Should().Be(job.OperationId);
        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;

        // The writer stops here: it never reaches RequeueAsync.
        (await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null
        })).Should().BeTrue();

        (await harness.Queue.GetQueueDepthAsync()).Should().Be(1);
        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(job.OperationId);
    }

    [IntegrationTest]
    public async Task TrySetAsync_ClaimedJobReturnedToQueuedWithRetryDelay_IsNotClaimableBeforeTheDelay()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "requeue-lost-with-backoff");
        (await harness.Queue.TryClaimAsync("worker-that-dies")).Should().Be(job.OperationId);
        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        var writerNow = DateTimeOffset.UtcNow;

        (await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            UpdatedAt = writerNow,
            NextRetryAt = writerNow.AddSeconds(3)
        })).Should().BeTrue();

        (await harness.Queue.TryClaimAsync("worker-b")).Should().BeNull();
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(job.OperationId);
    }

    [IntegrationTest]
    public async Task RemoveAsync_QueuedJob_IsNoLongerClaimable()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "removed");

        await harness.Queue.RemoveAsync(job.OperationId);

        (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ReconcileStaleClaimsAsync_LeavesAHeldClaimAndTheQueueUntouched()
    {
        await using var harness = await CreateHarnessAsync();
        var held = await CreateEnqueuedJobAsync(harness, "held");
        var waiting = await CreateEnqueuedJobAsync(harness, "waiting");
        (await harness.Queue.TryClaimAsync("worker-a")).Should().NotBeNull();
        await harness.AgeClaimAsync(held.OperationId, TimeSpan.FromMinutes(5));
        await harness.AgeClaimAsync(waiting.OperationId, TimeSpan.FromMinutes(5));

        await harness.Queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        (await harness.Queue.GetQueueDepthAsync()).Should().Be(1);
        var statuses = new[]
        {
            (await harness.JobStore.GetAsync(held.OperationId))!.Status,
            (await harness.JobStore.GetAsync(waiting.OperationId))!.Status
        };
        statuses.Should().BeEquivalentTo([ExecutionJobStatus.Provisioning, ExecutionJobStatus.Queued]);
    }

    private Task<PostgresJobSubstrateHarness> CreateHarnessAsync()
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobQueueTests));

    private static async Task<ExecutionJobRecord> CreateEnqueuedJobAsync(
        PostgresJobSubstrateHarness harness,
        string name,
        OperationPriority priority = OperationPriority.Normal,
        DateTimeOffset? createdAt = null,
        ExecutionJobKind kind = ExecutionJobKind.Geoprocessing,
        string? runtimeProfile = null)
    {
        var job = CreateQueuedJob(name, priority, createdAt, kind, runtimeProfile);
        (await harness.JobStore.TryCreateAsync(job)).Should().BeTrue();
        await harness.Queue.EnqueueAsync(job.OperationId, priority);
        return job;
    }

    private static ExecutionJobRecord CreateQueuedJob(
        string name,
        OperationPriority priority = OperationPriority.Normal,
        DateTimeOffset? createdAt = null,
        ExecutionJobKind kind = ExecutionJobKind.Geoprocessing,
        string? runtimeProfile = null)
    {
        var created = createdAt ?? DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = $"{name}-{Guid.NewGuid():N}",
            Status = ExecutionJobStatus.Queued,
            Priority = priority,
            CreatedAt = created,
            UpdatedAt = created,
            Spec = new ExecutionJobSpec
            {
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "integration-test",
                WorkloadName = "postgres-job-queue",
                RuntimeProfile = runtimeProfile
            }
        };
    }
}
