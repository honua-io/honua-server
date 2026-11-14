// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;
using Honua.Infrastructure.Progress;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed partial class TileOperationJobTests
{
    [UnitTheory]
    [InlineData(ExecutionJobStatus.Queued, OperationStatus.Queued)]
    [InlineData(ExecutionJobStatus.Running, OperationStatus.Processing)]
    [InlineData(ExecutionJobStatus.Cancelled, OperationStatus.Cancelled)]
    public async Task Get_AfterProgressExpires_ProjectsRetainedJobWithoutRecreatingSnapshot(
        ExecutionJobStatus jobStatus, OperationStatus expectedStatus)
    {
        var jobs = new InMemoryExecutionJobStore();
        var progress = new ComparingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progress, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobs, progress, [backend], new TileCacheBatchOptions());
        var request = SeedRequest();
        var jobId = await service.SubmitAsync(new TileOperationSubmission(request, Admin));
        var record = (await jobs.GetAsync(jobId))!;
        await jobs.SetAsync(record with { Status = jobStatus });
        await progress.DeleteProgressAsync(jobId);

        var response = await service.GetAsync(jobId);

        response!.Status.Should().Be(expectedStatus);
        response.Operation.Should().Be(request.Operation);
        response.TileMatrixSetId.Should().Be(request.TileMatrixSetId);
        response.DownloadUrl.Should().BeNull();
        response.PublishedArtifact.Should().BeNull();
        (await progress.GetProgressAsync(jobId)).Should().BeNull();
    }

    [UnitTheory]
    [InlineData(ExecutionJobStatus.Cancelled)]
    [InlineData(ExecutionJobStatus.Queued)]
    public void Get_NonSuccessfulDurableJob_HidesPublishedArtifact(ExecutionJobStatus status)
    {
        var initial = TileOperationProgress.CreateInitial("tile-result", "export", null, 1, "WebMercatorQuad");
        var completed = initial with
        {
            Status = OperationStatus.Completed,
            DownloadUrl = "https://example.test/tiles.pmtiles",
            PublishedArtifact = PublishedTiles()
        };
        var record = new ExecutionJobRecord
        {
            OperationId = initial.JobId,
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.TileCache,
                Backend = "local",
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                WorkloadName = "tiles"
            }
        };

        var response = (TileOperationProgress)ExecutionJobProgressReader.Project(completed, record);

        response.DownloadUrl.Should().BeNull();
        response.PublishedArtifact.Should().BeNull();
        completed.PublishedArtifact.Should().NotBeNull();
    }

    [UnitTest]
    public async Task Start_WorkerFinishesDuringDurableCreation_PreservesPublishedResult()
    {
        var jobStore = new InMemoryExecutionJobStore();
        var progressStore = new AcceptingInMemoryProgressStore();
        var backend = new LocalBatchComputeBackend(progressStore, [Substitute.For<IJobCancellationNotifier>()]);
        var service = CreateService(jobStore, progressStore, [backend], new TileCacheBatchOptions());
        jobStore.OnCreated = async job =>
        {
            var completed = TileOperationProgress.CreateInitial(job.OperationId, "seed", null, 1, "WebMercatorQuad") with
            {
                Status = OperationStatus.Completed,
                DownloadUrl = "https://example.test/tiles.pmtiles",
                PublishedArtifact = PublishedTiles()
            };
            await progressStore.SetProgressAsync(job.OperationId, completed);
            await jobStore.SetAsync(job with { Status = ExecutionJobStatus.Succeeded });
        };

        var jobId = await service.SubmitAsync(new TileOperationSubmission(SeedRequest(), Admin));

        var response = await service.GetAsync(jobId);
        response!.Status.Should().Be(OperationStatus.Completed);
        response.DownloadUrl.Should().Be("https://example.test/tiles.pmtiles");
        response.PublishedArtifact.Should().BeEquivalentTo(PublishedTiles());
    }

    private static PMTilesArtifactDescriptor PublishedTiles() => new()
    {
        ArtifactId = "tiles",
        StorageProvider = CloudStorageProvider.Local,
        Bucket = "test",
        ObjectKey = "tiles.pmtiles",
        ContentType = "application/vnd.pmtiles",
        SizeBytes = 1024,
        UrlStrategy = PMTilesUrlStrategy.PublicUrl,
        AccessUrl = "https://example.test/tiles.pmtiles",
        PublishedAt = DateTimeOffset.UnixEpoch,
        MinZoom = 0,
        MaxZoom = 2
    };
}
