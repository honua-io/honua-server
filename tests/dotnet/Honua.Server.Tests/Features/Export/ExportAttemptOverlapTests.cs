// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Io.Export;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

public sealed partial class ExportJobTests
{
    [IntegrationTheory]
    [InlineData(OperationStatus.Processing)]
    [InlineData(null)]
    public async Task Export_AdmissionOvertakenAfterReadingProgress_PreservesReplacementSnapshot(OperationStatus? initialStatus)
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        using var nodes = new ExportNodes(progress);
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        await PrepareAdmissionProgressAsync(progress, job, initialStatus);
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        var read = NewSignal();
        var resumeAdmission = NewSignal();
        var upload = NewSignal();
        var resumeUpload = NewSignal();
        var pausedStore = PauseProgressRead(progress, job.JobId, (read, resumeAdmission, ProgressReadPause.AfterSnapshot));
        PauseFirstUpload(nodes.Storage, (upload, resumeUpload));
        var staleContext = PublishingContext();
        var leaseLost = false;
        staleContext.ThrowIfExecutionLeaseLostAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            leaseLost ? Task.FromException(new OperationCanceledException("Attempt replaced")) : Task.CompletedTask);
        staleContext.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        var staleAttempt = nodes.CreateExecutor(pausedStore).ExecuteAsync(record, staleContext, CancellationToken.None);
        Task<JobExecutionResult>? replacementAttempt = null;
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(30));
            leaseLost = true;
            replacementAttempt = nodes.CreateExecutor().ExecuteAsync(record, PublishingContext(), CancellationToken.None);
            await upload.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var replacement = await progress.GetProgressAsync<ExportProgress>(job.JobId);

            resumeAdmission.TrySetResult();
            Func<Task> retire = async () => await staleAttempt;
            await retire.Should().ThrowAsync<OperationCanceledException>();
            (await progress.GetProgressAsync<ExportProgress>(job.JobId)).Should().Be(replacement,
                "a delayed admission must compare the snapshot it actually read");
        }
        finally
        {
            resumeAdmission.TrySetResult();
            resumeUpload.TrySetResult();
            await FinishAttemptsAsync(staleAttempt, replacementAttempt);
        }
    }

    [IntegrationTest]
    public async Task Export_AdmissionOvertakenBeforeReadingProgress_PreservesReplacementSnapshot()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        using var nodes = new ExportNodes(progress);
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        var reading = NewSignal();
        var resumeAdmission = NewSignal();
        var uploading = NewSignal();
        var resumeUpload = NewSignal();
        var pausedStore = PauseProgressRead(progress, job.JobId, (reading, resumeAdmission, ProgressReadPause.BeforeSnapshot));
        var staleContext = PublishingContext();
        var leaseLost = false;
        staleContext.ThrowIfExecutionLeaseLostAsync(Arg.Any<CancellationToken>()).Returns(_ =>
            leaseLost ? Task.FromException(new OperationCanceledException("Attempt replaced")) : Task.CompletedTask);
        staleContext.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        PauseFirstUpload(nodes.Storage, (uploading, resumeUpload));
        var staleAttempt = nodes.CreateExecutor(pausedStore).ExecuteAsync(record, staleContext, CancellationToken.None);
        Task<JobExecutionResult>? replacementAttempt = null;
        try
        {
            await reading.Task.WaitAsync(TimeSpan.FromSeconds(30));
            leaseLost = true;
            replacementAttempt = nodes.CreateExecutor().ExecuteAsync(record, PublishingContext(), CancellationToken.None);
            await uploading.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var replacement = await progress.GetProgressAsync<ExportProgress>(job.JobId);
            resumeAdmission.TrySetResult();
            Func<Task> retire = async () => await staleAttempt;
            await retire.Should().ThrowAsync<OperationCanceledException>();
            (await progress.GetProgressAsync<ExportProgress>(job.JobId)).Should().Be(replacement,
                "a retired attempt must not replace progress it first reads after losing its lease");
        }
        finally
        {
            resumeAdmission.TrySetResult();
            resumeUpload.TrySetResult();
            await FinishAttemptsAsync(staleAttempt, replacementAttempt);
        }
    }

    [IntegrationTest]
    public async Task Export_OverlappingAttemptCleanup_PreservesReplacementScratchFile()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        using var nodes = new ExportNodes(new ComparingInMemoryProgressStore());
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        var publishing = NewSignal();
        var retireFirst = NewSignal();
        var uploadingReplacement = NewSignal();
        var finishReplacement = NewSignal();
        var firstContext = PausePublication((publishing, retireFirst));
        PauseReplacementUpload(nodes.Storage, (uploadingReplacement, finishReplacement));
        var firstAttempt = nodes.CreateExecutor().ExecuteAsync(record, firstContext, CancellationToken.None);
        Task<JobExecutionResult>? replacementAttempt = null;
        try
        {
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(30));
            replacementAttempt = nodes.CreateExecutor().ExecuteAsync(record, PublishingContext(), CancellationToken.None);
            await uploadingReplacement.Task.WaitAsync(TimeSpan.FromSeconds(30));
            retireFirst.TrySetResult();
            Func<Task> retire = async () => await firstAttempt;
            await retire.Should().ThrowAsync<OperationCanceledException>();

            finishReplacement.TrySetResult();
            (await replacementAttempt).Status.Should().Be(ExecutionJobStatus.Succeeded,
                "retiring an older attempt must not delete the replacement's output");
        }
        finally
        {
            retireFirst.TrySetResult();
            finishReplacement.TrySetResult();
            await FinishAttemptsAsync(firstAttempt, replacementAttempt);
        }
    }

    private static async Task PrepareAdmissionProgressAsync(IUniversalProgressStore progress,
        ExportJob job, OperationStatus? initialStatus)
    {
        await progress.DeleteProgressAsync(job.JobId);
        if (initialStatus.HasValue)
        {
            await progress.SetProgressAsync(job.JobId,
                ExportProgress.CreateInitial(job.JobId, job.Format, job.ServiceName, 1, 1) with
                { Status = initialStatus.Value, StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) });
        }
    }

    private static void PauseFirstUpload(ICloudFileStorage storage,
        (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        var uploads = 0;
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref uploads) == 1)
            {
                signals.Reached.TrySetResult();
                await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
            }
            return ExportJobTestData.Uploaded("export-file", "text/csv");
        });
    }

    private static IJobExecutionContext PausePublication(
        (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            signals.Reached.TrySetResult();
            await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return false;
        });
        return context;
    }

    private static void PauseReplacementUpload(ICloudFileStorage storage,
        (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        var uploads = 0;
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref uploads) == 1)
                return ExportJobTestData.Uploaded("first-file", "text/csv");
            signals.Reached.TrySetResult();
            await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return ExportJobTestData.Uploaded("replacement-file", "text/csv");
        });
    }

    private static async Task FinishAttemptsAsync(Task<JobExecutionResult> retired, Task<JobExecutionResult>? successor)
    {
        var cleanupError = await Record.ExceptionAsync(async () => await retired);
        if (cleanupError is not null)
            cleanupError.Should().BeAssignableTo<OperationCanceledException>();
        if (successor is not null)
            (await successor).Status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    private static IUniversalProgressStore PauseProgressRead(
        ComparingInMemoryProgressStore progress,
        string jobId,
        (TaskCompletionSource Read, TaskCompletionSource Resume, ProgressReadPause Pause) signals)
    {
        var store = Substitute.For<IUniversalProgressStore>();
        store.GetProgressAsync<ExportProgress>(jobId, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (signals.Pause == ProgressReadPause.BeforeSnapshot)
            {
                signals.Read.TrySetResult();
                await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return await progress.GetProgressAsync<ExportProgress>(jobId);
            }
            var snapshot = await progress.GetProgressAsync<ExportProgress>(jobId);
            signals.Read.TrySetResult();
            await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return snapshot;
        });
        store.SetProgressAsync(jobId, Arg.Any<IOperationProgress>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.SetProgressAsync(jobId, call.ArgAt<IOperationProgress>(1), call.ArgAt<TimeSpan?>(2)));
        store.TrySetProgressAsync(Arg.Any<OperationProgressWrite>(), Arg.Any<OperationStatus>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.TrySetProgressAsync(call.ArgAt<OperationProgressWrite>(0), call.ArgAt<OperationStatus>(1)));
        store.TryReplaceProgressAsync(Arg.Any<OperationProgressWrite>(), Arg.Any<IOperationProgress?>(), Arg.Any<CancellationToken>())
            .Returns(call => progress.TryReplaceProgressAsync(call.ArgAt<OperationProgressWrite>(0), call.ArgAt<IOperationProgress?>(1)));
        return store;
    }

    private static IJobExecutionContext PublishingContext()
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        return context;
    }

    private enum ProgressReadPause { BeforeSnapshot, AfterSnapshot }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
