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
    public async Task TryClaimAsync_OrdersByPriorityThenTimeEnqueued()
    {
        await using var harness = await CreateHarnessAsync();
        var normalFirst = await CreateEnqueuedJobAsync(harness, "normal-first", OperationPriority.Normal);
        var low = await CreateEnqueuedJobAsync(harness, "low", OperationPriority.Low);
        var normalSecond = await CreateEnqueuedJobAsync(harness, "normal-second", OperationPriority.Normal);
        var critical = await CreateEnqueuedJobAsync(harness, "critical", OperationPriority.Critical);
        var high = await CreateEnqueuedJobAsync(harness, "high", OperationPriority.High);

        var order = new List<string?>();
        for (var claim = 0; claim < 5; claim++)
        {
            order.Add(await harness.Queue.TryClaimAsync("worker-a"));
        }

        order.Should().Equal(critical.OperationId, high.OperationId, normalFirst.OperationId, normalSecond.OperationId, low.OperationId);
        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task TryClaimAsync_RequeuedJob_GoesBehindJobsAlreadyWaitingInItsBand()
    {
        await using var harness = await CreateHarnessAsync();
        var handedBack = await CreateEnqueuedJobAsync(harness, "handed-back");
        var waiting = await CreateEnqueuedJobAsync(harness, "waiting");
        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(handedBack.OperationId);
        var claimed = (await harness.JobStore.GetAsync(handedBack.OperationId))!;
        (await harness.JobStore.TrySetAsync(claimed with
        {
            Status = ExecutionJobStatus.Queued,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null,
            AttemptCount = 0
        })).Should().BeTrue();
        await harness.Queue.RequeueAsync(handedBack.OperationId);

        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(waiting.OperationId);
        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(handedBack.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_AfterAClaimWhoseReplyWasLost_ReturnsThatJobWithOneAttempt()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "lost-reply");
        var other = await CreateEnqueuedJobAsync(harness, "other");
        const string workerId = "worker-with-lost-reply";
        var connectionThatLostTheReply = new PostgresJobQueue(harness.Database, Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresJobQueue>.Instance);
        (await connectionThatLostTheReply.TryClaimAsync(workerId)).Should().Be(job.OperationId);
        harness.Queue.MarkUnconfirmedClaim(workerId);

        (await harness.Queue.TryClaimAsync(workerId)).Should().Be(job.OperationId);

        (await harness.JobStore.GetAsync(job.OperationId))!.AttemptCount.Should().Be(1);
        (await harness.JobStore.GetAsync(other.OperationId))!.Status.Should().Be(ExecutionJobStatus.Queued);
        (await harness.Queue.TryClaimAsync(workerId)).Should().Be(other.OperationId, "the recovery check runs once");
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
        for (var index = 0; index < 150; index++)
        {
            var delayed = await CreateEnqueuedJobAsync(harness, $"delayed-{index:D3}");
            await harness.Queue.RequeueAsync(delayed.OperationId, visibleAfter: TimeSpan.FromMinutes(10));
        }

        var ready = await CreateEnqueuedJobAsync(harness, "ready");

        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(ready.OperationId);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenManyJobsOfAnotherKindPrecedeAReadyJob_ClaimsTheReadyJob()
    {
        await using var harness = await CreateHarnessAsync();
        for (var index = 0; index < 150; index++)
        {
            await CreateEnqueuedJobAsync(harness, $"import-{index:D3}", kind: ExecutionJobKind.FileImport);
        }

        var ready = await CreateEnqueuedJobAsync(harness, "geoprocessing");

        (await harness.Queue.TryClaimAsync("worker-a", new HashSet<ExecutionJobKind> { ExecutionJobKind.Geoprocessing }))
            .Should().Be(ready.OperationId);
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(150);
    }

    [IntegrationTest]
    public async Task TryClaimAsync_WhenFirstCandidateIsLockedByAnotherClaim_ClaimsTheNextCandidate()
    {
        await using var harness = await CreateHarnessAsync();
        var contested = await CreateEnqueuedJobAsync(harness, "contested");
        var next = await CreateEnqueuedJobAsync(harness, "next");

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
        var first = await CreateEnqueuedJobAsync(harness, "first");
        var second = await CreateEnqueuedJobAsync(harness, "second");

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

    [IntegrationTheory]
    [MemberData(nameof(EveryJobKind))]
    public async Task ReconcileStaleClaimsAsync_LocalJobCreatedButNeverEnqueued_IsEnqueued(ExecutionJobKind kind)
    {
        await using var harness = await CreateHarnessAsync();
        var created = CreateQueuedJob("never-enqueued", OperationPriority.High, kind: kind);
        (await harness.JobStore.TryCreateAsync(created)).Should().BeTrue();
        var recent = CreateQueuedJob("just-created", kind: kind);
        (await harness.JobStore.TryCreateAsync(recent)).Should().BeTrue();
        await AgeLastWriteAsync(harness, created.OperationId, TimeSpan.FromMinutes(2));

        await harness.Queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        (await harness.IsInQueueAsync(created.OperationId)).Should().BeTrue();
        (await harness.IsInQueueAsync(recent.OperationId)).Should().BeFalse("its producer may still be about to enqueue it");
        (await harness.Queue.TryClaimAsync("worker-a", new HashSet<ExecutionJobKind> { kind })).Should().Be(created.OperationId);
    }

    public static TheoryData<ExecutionJobKind> EveryJobKind()
        => new(Enum.GetValues<ExecutionJobKind>());

    [IntegrationTest]
    public async Task ReconcileStaleClaimsAsync_ManualRetryThatNeverRequeued_IsEnqueued()
    {
        await using var harness = await CreateHarnessAsync();
        var job = await CreateEnqueuedJobAsync(harness, "manual-retry");
        (await harness.Queue.TryClaimAsync("worker-a")).Should().Be(job.OperationId);
        var claimed = (await harness.JobStore.GetAsync(job.OperationId))!;
        (await harness.JobStore.TrySetAsync(claimed with { Status = ExecutionJobStatus.Failed, CompletedAt = DateTimeOffset.UtcNow }))
            .Should().BeTrue();
        var failed = (await harness.JobStore.GetAsync(job.OperationId))!;
        (await harness.JobStore.TrySetAsync(failed with
        {
            Status = ExecutionJobStatus.Queued,
            CompletedAt = null,
            ClaimedBy = null,
            ClaimedAt = null,
            LastHeartbeatAt = null
        })).Should().BeTrue();
        (await harness.IsInQueueAsync(job.OperationId)).Should().BeFalse("the retry never reached RequeueAsync");
        await AgeLastWriteAsync(harness, job.OperationId, TimeSpan.FromMinutes(2));

        await harness.Queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        (await harness.Queue.TryClaimAsync("worker-b")).Should().Be(job.OperationId);
    }

    [IntegrationTest]
    public async Task ReconcileStaleClaimsAsync_RemoteBackendJob_IsLeftForItsBackend()
    {
        await using var harness = await CreateHarnessAsync();
        var remote = CreateQueuedJob("remote", backend: "aws-batch");
        (await harness.JobStore.TryCreateAsync(remote)).Should().BeTrue();
        await AgeLastWriteAsync(harness, remote.OperationId, TimeSpan.FromMinutes(2));

        await harness.Queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        await harness.Queue.EnqueueAsync(remote.OperationId);
        await harness.Queue.RequeueAsync(remote.OperationId);

        (await harness.IsInQueueAsync(remote.OperationId)).Should().BeFalse();
        (await harness.Queue.TryClaimAsync("worker-a")).Should().BeNull();
    }

    [IntegrationTest]
    public async Task ReconcileStaleClaimsAsync_CancellationRequestedJob_IsNotEnqueued()
    {
        await using var harness = await CreateHarnessAsync();
        var cancelling = CreateQueuedJob("cancelling") with { CancellationRequestedAt = DateTimeOffset.UtcNow };
        (await harness.JobStore.TryCreateAsync(cancelling)).Should().BeTrue();
        await AgeLastWriteAsync(harness, cancelling.OperationId, TimeSpan.FromMinutes(2));

        await harness.Queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

        (await harness.IsInQueueAsync(cancelling.OperationId)).Should().BeFalse();
    }

    private static Task AgeLastWriteAsync(PostgresJobSubstrateHarness harness, string operationId, TimeSpan age)
        => harness.ExecuteAsync(
            $"UPDATE {harness.JobsTable} SET updated_at = now() - @age WHERE operation_id = @operation_id",
            new NpgsqlParameter("age", age),
            new NpgsqlParameter("operation_id", operationId));

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
        string? runtimeProfile = null,
        string backend = LocalBatchComputeBackend.BackendId)
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
                Backend = backend,
                WorkloadName = "postgres-job-queue",
                RuntimeProfile = runtimeProfile
            }
        };
    }
}
