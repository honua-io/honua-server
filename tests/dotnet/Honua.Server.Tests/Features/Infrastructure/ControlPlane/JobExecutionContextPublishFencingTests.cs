// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Raster;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// The heart of #3089's publication protocol: artifact publication is fenced on the
/// claimed attempt and durable cancellation, and is idempotent so a retried attempt
/// (or a retried publish within one attempt) can never duplicate an artifact entry
/// in the durable job record.
/// </summary>
public sealed class JobExecutionContextPublishFencingTests
{
    private const string WorkerId = "worker-test";

    [UnitTest]
    public async Task PublishArtifact_StaleAttempt_IsFenced()
    {
        // The record was requeued and reclaimed: its attempt is now 2, while this
        // zombie context still holds claimed attempt 1 (same worker id).
        var reclaimed = CreateRunningJob(attemptCount: 2);
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(reclaimed.OperationId, Arg.Any<CancellationToken>()).Returns(reclaimed);

        using var context = CreateContext(reclaimed.OperationId, jobStore, claimedAttempt: 1);

        await context.PublishArtifactAsync("data:image/tiff;base64,AAAA", CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishArtifact_CancellationRequested_IsFenced()
    {
        var cancelling = CreateRunningJob(attemptCount: 1) with
        {
            CancellationRequestedAt = DateTimeOffset.UtcNow
        };
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(cancelling.OperationId, Arg.Any<CancellationToken>()).Returns(cancelling);

        using var context = CreateContext(cancelling.OperationId, jobStore, claimedAttempt: 1);

        await context.PublishArtifactAsync("data:image/tiff;base64,AAAA", CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishArtifact_IdenticalReference_IsIdempotent()
    {
        var reference = "data:image/tiff;base64,AAAA";
        var published = CreateRunningJob(attemptCount: 1) with
        {
            ArtifactReferences = [reference]
        };
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(published.OperationId, Arg.Any<CancellationToken>()).Returns(published);

        using var context = CreateContext(published.OperationId, jobStore, claimedAttempt: 1);

        await context.PublishArtifactAsync(reference, CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishArtifact_SameOutputSameAttempt_ReplacesInsteadOfDuplicating()
    {
        var original = RasterOutputJson.Serialize(CreateDescriptor(checksumSeed: 'a'));
        var republished = RasterOutputJson.Serialize(CreateDescriptor(checksumSeed: 'b'));
        var job = CreateRunningJob(attemptCount: 1) with
        {
            ArtifactReferences = [original]
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        ExecutionJobRecord? written = null;
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                written = call.Arg<ExecutionJobRecord>();
                return true;
            });

        using var context = CreateContext(job.OperationId, jobStore, claimedAttempt: 1);

        await context.PublishArtifactAsync(republished, CancellationToken.None);

        written.Should().NotBeNull();
        written!.ArtifactReferences.Should().ContainSingle()
            .Which.Should().Be(republished);
    }

    [UnitTest]
    public async Task PublishArtifact_IdenticalDescriptor_IsIdempotent()
    {
        var reference = RasterOutputJson.Serialize(CreateDescriptor(checksumSeed: 'a'));
        var job = CreateRunningJob(attemptCount: 1) with
        {
            ArtifactReferences = [reference]
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);

        using var context = CreateContext(job.OperationId, jobStore, claimedAttempt: 1);

        await context.PublishArtifactAsync(reference, CancellationToken.None);

        await jobStore.DidNotReceive().TrySetAsync(
            Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task PublishArtifact_MatchingAttempt_AppendsUnderFence()
    {
        var job = CreateRunningJob(attemptCount: 3);
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);

        using var context = CreateContext(job.OperationId, jobStore, claimedAttempt: 3);

        await context.PublishArtifactAsync("data:image/tiff;base64,AAAA", CancellationToken.None);

        await jobStore.Received(1).TrySetAsync(
            Arg.Is<ExecutionJobRecord>(record =>
                record.ArtifactReferences.Count == 1
                && record.ArtifactReferences[0] == "data:image/tiff;base64,AAAA"),
            Arg.Any<TimeSpan?>(),
            Arg.Any<CancellationToken>());
    }

    private static JobExecutionContext CreateContext(
        string operationId,
        IExecutionJobStore jobStore,
        int claimedAttempt)
        => new(
            operationId, WorkerId, jobStore, null, JobHeartbeatPolicy.Default,
            null, NullLogger.Instance, claimedAttempt);

    private static ExecutionJobRecord CreateRunningJob(int attemptCount)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = "job-fence-1",
            Status = ExecutionJobStatus.Running,
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now,
            ClaimedBy = WorkerId,
            ClaimedAt = now,
            LastHeartbeatAt = now,
            AttemptCount = attemptCount,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
    }

    private static StagedObjectRasterOutputDescriptor CreateDescriptor(char checksumSeed) => new()
    {
        JobId = "job-fence-1",
        AttemptNumber = 1,
        OutputName = "output1",
        Content = new RasterContentIdentity
        {
            SizeBytes = 10,
            MediaType = "image/tiff",
            Checksum = new RasterChecksum("sha256", new string(checksumSeed, 64)),
        },
        ProducingEngine = RasterOutputContract.GdalWorkerEngine,
        Provider = CloudStorageProvider.Local,
        StoreReference = "gp-outputs",
        ObjectKey = "gp/outputs/job-fence-1/a1/output1/result.tif",
    };
}
