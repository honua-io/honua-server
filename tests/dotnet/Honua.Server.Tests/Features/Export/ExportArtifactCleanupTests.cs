// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

public sealed partial class ExportJobTests
{
    [IntegrationTheory]
    [InlineData(ExecutionJobStatus.Queued)]
    [InlineData(ExecutionJobStatus.Failed)]
    public async Task Export_InterruptedAfterUpload_DeletesAbandonedArtifact(ExecutionJobStatus interruptedStatus)
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        using var nodes = new ExportNodes(new ComparingInMemoryProgressStore());
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        nodes.Storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExportJobTestData.Uploaded("abandoned-file", "text/csv"), ExportJobTestData.Uploaded("export-file", "text/csv"));
        nodes.Storage.GetPresignedUrlAsync("abandoned-file", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/abandoned.csv");
        var uploaded = NewSignal();
        using var worker = harness.CreateWorker(PauseCompletedExport(nodes, uploaded), nodes.Terminal);
        worker.RequeueOnShutdown = interruptedStatus == ExecutionJobStatus.Queued;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            await worker.StopAsync(deadline.Token);
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(60));
        }

        var interrupted = (await harness.JobStore.GetAsync(job.JobId))!;
        interrupted.Status.Should().Be(interruptedStatus);
        interrupted.CleanupArtifactReferences.Should().Contain("abandoned-file");
        if (interruptedStatus == ExecutionJobStatus.Queued)
        {
            await CompleteReplacementExportAsync(harness, nodes, job.JobId);
        }

        await nodes.Storage.Received().DeleteAsync("abandoned-file", Arg.Any<CancellationToken>());
        await nodes.Storage.DidNotReceive().DeleteAsync("export-file", Arg.Any<CancellationToken>());
    }

    private static IJobExecutor PauseCompletedExport(ExportNodes nodes, TaskCompletionSource uploaded)
    {
        var executor = Substitute.For<IJobExecutor>();
        executor.Kind.Returns(ExecutionJobKind.Export);
        executor.AcceptedRuntimeProfiles.Returns(RuntimeProfiles.DefaultAccepted);
        executor.ExecuteAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<IJobExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var token = call.Arg<CancellationToken>();
                var result = await nodes.CreateExecutor().ExecuteAsync(
                    call.Arg<ExecutionJobRecord>(), call.Arg<IJobExecutionContext>(), token);
                result.Status.Should().Be(ExecutionJobStatus.Succeeded);
                uploaded.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return result;
            });
        return executor;
    }

    private static async Task CompleteReplacementExportAsync(PostgresJobSubstrateHarness harness, ExportNodes nodes, string jobId)
    {
        using var replacement = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal, harness.CreateNodeJobStore());
        await replacement.StartAsync(CancellationToken.None);
        try
        {
            var finished = await harness.WaitForJobAsync(jobId, IsEnded, TimeSpan.FromSeconds(60));
            finished.Status.Should().Be(ExecutionJobStatus.Succeeded, finished.ErrorMessage);
            finished.ArtifactReferences.Should().Equal("export-file");
        }
        finally
        {
            await replacement.StopAsync(CancellationToken.None);
        }
    }
}
