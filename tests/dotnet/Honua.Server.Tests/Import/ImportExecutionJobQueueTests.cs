// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Import;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed partial class ImportExecutionJobQueueTests
{
    [UnitTest]
    public async Task SubmitAsync_EnqueueFails_RetainsRequestForDispatchRepairAndProgressRecovery()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var queue = Substitute.For<IJobQueue>();
        queue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("queue unavailable")));
        var acceptingNode = Manager(jobs, queue);
        var request = Request();

        await acceptingNode.SubmitAsync(request, Initial(request));

        var recoveringNode = Manager(jobs, queue);
        var record = (await jobs.GetAsync(request.JobId!))!;
        recoveringNode.ReadRequest(record).Should().BeEquivalentTo(request);
        (await recoveringNode.ProgressStore.GetProgressAsync(request.JobId!))!.Status.Should().Be(GeoservicesImportStatus.Queued);
        (await recoveringNode.ProgressStore.GetActiveJobIdsAsync()).Should().Contain(request.JobId!);
    }

    [UnitTest]
    public async Task SubmitAsync_RetriedAfterExecutionStarts_PreservesRunningProgressAndOriginalRequest()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = Manager(jobs, Substitute.For<IJobQueue>());
        var request = Request();
        var initial = Initial(request);
        await manager.SubmitAsync(request, initial);
        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with
        {
            Status = GeoservicesImportStatus.InsertingFeatures,
            FeaturesProcessed = 42
        });

        await manager.SubmitAsync(request with { TableName = "changed_on_retry" }, initial);

        var record = (await jobs.GetAsync(request.JobId!))!;
        manager.ReadRequest(record).Should().BeEquivalentTo(request);
        var progress = await manager.ProgressStore.GetProgressAsync(request.JobId!);
        progress!.Status.Should().Be(GeoservicesImportStatus.InsertingFeatures);
        progress.FeaturesProcessed.Should().Be(42);
    }

    [UnitTest]
    public async Task RequestCancellationAsync_UnclaimedJob_CancelsDurablyWithoutSeedingProgress()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var queue = Substitute.For<IJobQueue>();
        var manager = Manager(jobs, queue);
        var request = Request();
        await manager.SubmitAsync(request, Initial(request));

        await manager.RequestCancellationAsync(request.JobId!);

        (await jobs.GetAsync(request.JobId!))!.Status.Should().Be(ExecutionJobStatus.Cancelled);
        var progress = await manager.ProgressStore.GetProgressAsync(request.JobId!);
        progress!.Status.Should().Be(GeoservicesImportStatus.Cancelled);
        progress.CompletedAt.Should().NotBeNull();
        await queue.Received(1).RemoveAsync(request.JobId!, Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RequestCancellationAsync_PreviouslyAttemptedJob_RemainsQueuedForCommitRecovery()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var queue = Substitute.For<IJobQueue>();
        var manager = Manager(jobs, queue);
        var request = Request();
        await manager.SubmitAsync(request, Initial(request));
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with { AttemptCount = 1 })).Should().BeTrue();

        await manager.RequestCancellationAsync(request.JobId!);

        var cancelling = (await jobs.GetAsync(request.JobId!))!;
        cancelling.Status.Should().Be(ExecutionJobStatus.Queued);
        cancelling.CancellationRequestedAt.Should().NotBeNull();
        await queue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RequestCancellationAsync_ClaimedJob_StampsCancellationWithoutReplacingWorkerState()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var queue = Substitute.For<IJobQueue>();
        var manager = Manager(jobs, queue);
        var request = Request();
        await manager.SubmitAsync(request, Initial(request));
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with { Status = ExecutionJobStatus.Running, ClaimedBy = "worker" })).Should().BeTrue();

        await manager.RequestCancellationAsync(request.JobId!);

        var cancelling = (await jobs.GetAsync(request.JobId!))!;
        cancelling.Status.Should().Be(ExecutionJobStatus.Running);
        cancelling.ClaimedBy.Should().Be("worker");
        cancelling.CancellationRequestedAt.Should().NotBeNull();
        await queue.DidNotReceive().RemoveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SetProgressAsync_LateWorkerReport_DoesNotOverwriteCancellation()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = Manager(jobs, Substitute.For<IJobQueue>());
        var request = Request();
        var initial = Initial(request);
        await manager.SubmitAsync(request, initial);
        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with { Status = GeoservicesImportStatus.Cancelled });

        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with { Status = GeoservicesImportStatus.InsertingFeatures });

        (await manager.ProgressStore.GetProgressAsync(request.JobId!))!.Status.Should().Be(GeoservicesImportStatus.Cancelled);
    }

    [UnitTheory]
    [InlineData(GeoservicesImportStatus.Completed)]
    [InlineData(GeoservicesImportStatus.NeedsReview)]
    [InlineData(GeoservicesImportStatus.Failed)]
    public async Task SetProgressAsync_CommittedImportAfterCancellation_PreservesOutcome(GeoservicesImportStatus status)
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = Manager(jobs, Substitute.For<IJobQueue>());
        var request = Request();
        var initial = Initial(request);
        await manager.SubmitAsync(request, initial);
        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with { Status = GeoservicesImportStatus.Cancelled });

        await manager.ProgressStore.SetProgressAsync(request.JobId!, initial with
        {
            Status = status,
            DataCommitted = true,
            FeaturesProcessed = 42,
            ErrorMessage = "Import was cancelled after the data was committed"
        });

        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(status);
        progress.FeaturesProcessed.Should().Be(42);
    }

    [UnitTest]
    public async Task GetProgressAsync_ExecutionEndsBeforeFinalProgress_ProjectsDurableFailure()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = Manager(jobs, Substitute.For<IJobQueue>());
        var request = Request();
        await manager.SubmitAsync(request, Initial(request));
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with
        {
            Status = ExecutionJobStatus.Failed,
            ErrorMessage = "Worker timed out",
            CompletedAt = DateTimeOffset.UtcNow
        })).Should().BeTrue();

        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;

        progress.Status.Should().Be(GeoservicesImportStatus.Failed);
        progress.ErrorMessage.Should().Be("Worker timed out");
        progress.CompletedAt.Should().NotBeNull();
    }

    [UnitTest]
    public async Task GetProgressAsync_SucceededJobWithoutProgress_ReturnsExpiredResult()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = Manager(jobs, Substitute.For<IJobQueue>());
        var request = Request();
        await manager.SubmitAsync(request, Initial(request));
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with { Status = ExecutionJobStatus.Succeeded })).Should().BeTrue();

        (await manager.ProgressStore.GetProgressAsync(request.JobId!)).Should().BeNull();
    }

    [UnitTest]
    public async Task GetProgressAsync_SucceededGeoServerJobWithoutProgress_ReturnsExpiredResult()
    {
        var jobs = ImportExecutionTestJobs.Create();
        var manager = new GeoServerImportJobManager(
            new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance),
            NullLogger<GeoServerImportJobManager>.Instance, jobs, Substitute.For<IJobQueue>());
        var request = new GeoServerImportRequest
        {
            JobId = Guid.NewGuid().ToString("N"),
            GeoServerRestUrl = "https://example.com/geoserver/rest",
            TargetHonuaUrl = "https://honua.example.com"
        };
        await manager.SubmitAsync(request, GeoServerImportProgress.CreateInitial(request.JobId!, request.GeoServerRestUrl, request.TargetHonuaUrl), CancellationToken.None);
        var record = (await jobs.GetAsync(request.JobId!))!;
        (await jobs.TrySetAsync(record with { Status = ExecutionJobStatus.Succeeded })).Should().BeTrue();

        (await manager.ProgressStore.GetProgressAsync(request.JobId!)).Should().BeNull();
    }

    private static GeoservicesImportJobManager Manager(IExecutionJobStore jobs, IJobQueue queue)
        => new(new UniversalProgressStore(null, NullLogger<UniversalProgressStore>.Instance),
            NullLogger<GeoservicesImportJobManager>.Instance, jobs, queue);

    private static GeoservicesImportRequest Request() => new()
    {
        JobId = Guid.NewGuid().ToString("N"),
        ServiceUrl = "https://example.com/arcgis/rest/services/Source/FeatureServer",
        LayerId = 0,
        TableName = "durable_source"
    };

    private static GeoservicesImportProgress Initial(GeoservicesImportRequest request)
        => GeoservicesImportProgress.CreateInitial(request.JobId!, request.ServiceUrl, request.LayerId, request.TableName);
}
