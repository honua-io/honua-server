// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Io.Export;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

public sealed partial class ExportJobTests
{
    [IntegrationTest]
    public async Task Export_CurrentLeaseAdmissionConflictsWithRetiredAttempt_RetriesAndSucceeds()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        using var nodes = new ExportNodes(progress);
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        var oldCasReached = NewSignal();
        var resumeOldCas = NewSignal();
        var successorRead = NewSignal();
        var resumeSuccessor = NewSignal();
        var oldStore = PauseProgressRead(progress, job.JobId, (NewSignal(), CompletedSignal(), ProgressReadPause.AfterSnapshot));
        PauseProgressReplacement(oldStore, progress, (oldCasReached, resumeOldCas));
        var oldContext = PublishingContext();
        oldContext.TryPublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);
        nodes.Storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExportJobTestData.Uploaded("export-file", "text/csv"));
        var oldAttempt = nodes.CreateExecutor(oldStore).ExecuteAsync(record, oldContext, CancellationToken.None);
        Task<JobExecutionResult>? successor = null;
        try
        {
            await oldCasReached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var successorStore = PauseProgressRead(progress, job.JobId, (successorRead, resumeSuccessor, ProgressReadPause.AfterSnapshot));
            successor = nodes.CreateExecutor(successorStore).ExecuteAsync(record, PublishingContext(), CancellationToken.None);
            await successorRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            resumeOldCas.TrySetResult();
            Func<Task> retire = async () => await oldAttempt;
            await retire.Should().ThrowAsync<OperationCanceledException>();
            resumeSuccessor.TrySetResult();
            (await successor).Status.Should().Be(ExecutionJobStatus.Succeeded,
                "a stale admission write must not terminally fail the attempt that still owns the lease");
        }
        finally
        {
            resumeOldCas.TrySetResult();
            resumeSuccessor.TrySetResult();
            await FinishAttemptsAsync(oldAttempt, successor);
        }
    }

    private static void PauseProgressReplacement(IUniversalProgressStore store,
        ComparingInMemoryProgressStore progress, (TaskCompletionSource Reached, TaskCompletionSource Resume) signals)
    {
        store.TryReplaceProgressAsync(Arg.Any<OperationProgressWrite>(), Arg.Any<IOperationProgress?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                signals.Reached.TrySetResult();
                await signals.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return await progress.TryReplaceProgressAsync(call.ArgAt<OperationProgressWrite>(0), call.ArgAt<IOperationProgress?>(1));
            });
    }

    private static TaskCompletionSource CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult();
        return signal;
    }
}
