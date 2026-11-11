// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;
using static Honua.Server.Tests.Features.Infrastructure.ControlPlane.PostgresJobQueueTestJobs;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// The job queue contract against PostgreSQL: a local queued job that lost its place in the queue
// is put back, so it is never stranded.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresJobQueueDispatchRepairTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
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

    [IntegrationTheory]
    [MemberData(nameof(EveryJobKind))]
    public async Task ReconcileStaleClaimsAsync_LocalJobCreatedButNeverEnqueued_IsEnqueued(ExecutionJobKind kind)
    {
        await using var harness = await CreateHarnessAsync();
        var created = CreateQueuedJob("never-enqueued", OperationPriority.High, new() { Kind = kind });
        (await harness.JobStore.TryCreateAsync(created)).Should().BeTrue();
        var recent = CreateQueuedJob("just-created", spec: new() { Kind = kind });
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
        var remote = CreateQueuedJob("remote", spec: new() { Backend = "aws-batch" });
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
        => PostgresJobSubstrateHarness.CreateAsync(database, nameof(PostgresJobQueueDispatchRepairTests));
}
