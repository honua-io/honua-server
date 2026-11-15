// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed partial class ImportJobExecutorTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_CommitAttestationFails_LeavesProgressRecoverable()
    {
        var request = PublishedGeoservicesRequest(Guid.NewGuid().ToString("N"), 4);
        var importer = new CommittedCancelGeoservicesImportService(CommittedResult(request, GeoservicesImportStatus.Completed));
        importer.Release.SetResult(true);
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));
        var context = Substitute.For<IJobExecutionContext>();
        context.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new IOException("Execution store unavailable"));

        var record = (await jobs.GetAsync(request.JobId!))!;
        Func<Task> execute = () => executor.ExecuteAsync(record, context, CancellationToken.None);

        await execute.Should().ThrowAsync<IOException>();
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        GeoservicesImportJobManager.IsTerminalStatus(progress.Status).Should().BeFalse();
    }

    [Theory]
    [InlineData(GeoservicesImportStatus.Completed, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.NeedsReview, ExecutionJobStatus.Succeeded)]
    [InlineData(GeoservicesImportStatus.Failed, ExecutionJobStatus.Failed)]
    [Trait("Category", "Unit")]
    public async Task ProcessJob_CancellationAfterImportCommit_PreservesCommittedOutcome(
        GeoservicesImportStatus outcome, ExecutionJobStatus executionStatus)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var request = PublishedGeoservicesRequest(jobId, 4);
        var importer = new CommittedCancelGeoservicesImportService(CommittedResult(request, outcome));
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider);
        await manager.SubmitAsync(request,
            GeoservicesImportProgress.CreateInitial(jobId, request.ServiceUrl, request.LayerId, request.TableName));
        await ClaimImportAsync(jobs, jobId);
        using var worker = new JobExecutionService(Substitute.For<IJobQueue>(), jobs, [executor],
            new ExecutionJobCancellationTokens(), [], null, NullLogger<JobExecutionService>.Instance);
        var process = typeof(JobExecutionService).GetMethod("ProcessJobAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var execution = (Task)process.Invoke(worker, [jobId, "import-worker", CancellationToken.None])!;
        try
        {
            await importer.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await manager.RequestCancellationAsync(jobId);
            await RecordCancellationAsync(manager, (await manager.ProgressStore.GetProgressAsync(jobId))!);
        }
        finally
        {
            importer.Release.TrySetResult(true);
            await execution.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var completed = (await jobs.GetAsync(jobId))!;
        completed.Status.Should().Be(executionStatus);
        completed.CommittedEffectReferences.Should().Contain($"honua:import:{jobId}:committed");
        var progress = (await manager.ProgressStore.GetProgressAsync(jobId))!;
        progress.Status.Should().Be(outcome);
        progress.DataCommitted.Should().BeTrue();
        progress.FeaturesProcessed.Should().Be(4);
        progress.PublishedLayerId.Should().Be(42);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ExecuteAsync_LicenseExpiresDuringCommitReceipt_PreservesCommittedDetails()
    {
        using var license = new CancellationTokenSource();
        var policy = Substitute.For<ILicenseOperationPolicy>();
        policy.OperationCancellation.Returns(license.Token);
        var request = PublishedGeoservicesRequest(Guid.NewGuid().ToString("N"), 4);
        var importer = new CommittedCancelGeoservicesImportService(CommittedResult(request, GeoservicesImportStatus.Completed));
        importer.Release.SetResult(true);
        using var provider = CreateGeoservicesProvider(importer);
        var (jobs, manager, executor) = CreateGeoservicesExecutor(provider, policy);
        await manager.SubmitAsync(request, GeoservicesImportProgress.CreateInitial(
            request.JobId!, request.ServiceUrl, request.LayerId, request.TableName));
        var context = Substitute.For<IJobExecutionContext>();
        context.RecordCommittedEffectAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            license.Cancel();
            throw new OperationCanceledException(license.Token);
        });

        var result = await executor.ExecuteAsync((await jobs.GetAsync(request.JobId!))!, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        var progress = (await manager.ProgressStore.GetProgressAsync(request.JobId!))!;
        progress.Status.Should().Be(GeoservicesImportStatus.Failed);
        progress.DataCommitted.Should().BeTrue();
        progress.FeaturesProcessed.Should().Be(4);
        progress.PublishedLayerId.Should().Be(42);
        progress.ErrorMessage.Should().Contain("already committed");
    }

    private static GeoservicesImportResult CommittedResult(
        GeoservicesImportRequest request, GeoservicesImportStatus outcome)
        => GeoservicesImportResult.CreateFailure(request.TableName, request.ServiceUrl, request.LayerId,
            "Imported rows were already committed") with
        {
            Success = outcome == GeoservicesImportStatus.Completed,
            NeedsReview = outcome == GeoservicesImportStatus.NeedsReview,
            DataCommitted = true,
            FeatureCount = 4,
            PublishedLayerId = 42
        };

    private static async Task ClaimImportAsync(IExecutionJobStore jobs, string jobId)
    {
        var record = (await jobs.GetAsync(jobId))!;
        (await jobs.TrySetAsync(record with
        {
            Status = ExecutionJobStatus.Provisioning,
            ClaimedBy = "import-worker",
            ClaimedAt = DateTimeOffset.UtcNow,
            AttemptCount = 1
        })).Should().BeTrue();
    }

}
