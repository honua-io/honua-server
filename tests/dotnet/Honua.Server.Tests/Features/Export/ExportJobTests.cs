// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;
using Honua.Io.Export;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Export;

// An accepted export is an Export execution job on the PostgreSQL job substrate: two nodes polling
// the same queue run it once, under the submitter's identity, and agree on one artifact.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
[Operation(Operations.Export)]
public sealed class ExportJobTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Export_ClaimedByTwoWorkers_RunsOnce()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        using var nodes = new ExportNodes(new ComparingInMemoryProgressStore());
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var nodeA = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal, harness.CreateNodeJobStore());
        var nodeB = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal, harness.CreateNodeJobStore());
        await Task.WhenAll(nodeA.StartAsync(CancellationToken.None), nodeB.StartAsync(CancellationToken.None));

        ExecutionJobRecord finished;
        try
        {
            finished = await harness.WaitForJobAsync(job.JobId, IsEnded, TimeSpan.FromSeconds(60));
        }
        finally
        {
            // Stopping both workers drains anything they still run, so a duplicate attempt would be counted below.
            await Task.WhenAll(nodeA.StopAsync(CancellationToken.None), nodeB.StopAsync(CancellationToken.None));
        }

        finished.Status.Should().Be(ExecutionJobStatus.Succeeded, finished.ErrorMessage);
        (await harness.JobStore.GetAsync(job.JobId))!.AttemptCount.Should().Be(1, "one node claimed the export");
        nodes.Streams.Should().Be(1, "the layer is read once across both nodes");
        await nodes.Storage.Received(1).UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
        nodes.ObservedSubmitter.Should().BeEquivalentTo(job.Submitter, "the worker runs the export as its submitter");
        var progress = await nodes.Progress.GetProgressAsync<ExportProgress>(job.JobId);
        progress!.Status.Should().Be(OperationStatus.Completed);
        progress.DownloadUrl.Should().Be("https://example.test/export.csv");
        (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
    }

    [IntegrationTest]
    public async Task Export_WhoseWorkerIsLost_IsTakenOverByAnotherNodeAndUploadedOnce()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        using var nodes = new ExportNodes(new ComparingInMemoryProgressStore()) { HoldFirstRead = true };
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var lostNode = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal, harness.CreateNodeJobStore());
        await lostNode.StartAsync(CancellationToken.None);
        JobExecutionService? survivor = null;
        try
        {
            await nodes.FirstReadStarted.WaitAsync(TimeSpan.FromSeconds(60));

            // The lost node stops heartbeating; the reconciler sees its claim expire and requeues the job.
            await harness.AgeClaimAsync(job.JobId, TimeSpan.FromHours(1));
            await harness.RunReconciliationSweepAsync(CancellationToken.None);
            survivor = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal, harness.CreateNodeJobStore());
            await survivor.StartAsync(CancellationToken.None);

            // The requeue waits out the retry policy's first backoff before the survivor can claim it.
            var finished = await harness.WaitForJobAsync(job.JobId, IsEnded, TimeSpan.FromSeconds(180));
            finished.Status.Should().Be(ExecutionJobStatus.Succeeded, finished.ErrorMessage);
            finished.AttemptCount.Should().Be(2, "the surviving node ran the second attempt");
            nodes.Streams.Should().Be(2, "the lost attempt started reading before it was taken over");
            await nodes.Storage.Received(1).UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
            (await nodes.Progress.GetProgressAsync<ExportProgress>(job.JobId))!.Status.Should().Be(OperationStatus.Completed);
        }
        finally
        {
            await lostNode.StopAsync(CancellationToken.None);
            if (survivor != null)
            {
                await survivor.StopAsync(CancellationToken.None);
            }
        }
    }

    [IntegrationTest]
    public async Task Export_CancelledOnAnotherNodeDuringUpload_DoesNotPublishDownload()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        using var nodes = new ExportNodes(new ComparingInMemoryProgressStore());
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var otherNode = harness.CreateNodeJobStore();
        nodes.Storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                while (true)
                {
                    var current = (await otherNode.GetAsync(job.JobId))!;
                    if (await otherNode.TrySetAsync(current with { CancellationRequestedAt = DateTimeOffset.UtcNow }))
                    {
                        break;
                    }
                }

                return ExportJobTestData.Uploaded("export-file", "text/csv");
            });
        var worker = harness.CreateWorker(nodes.CreateExecutor(), nodes.Terminal);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var finished = await harness.WaitForJobAsync(job.JobId, IsEnded, TimeSpan.FromSeconds(60));
            finished.Status.Should().Be(ExecutionJobStatus.Cancelled);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        var progress = (ExportProgress)(await new ExecutionJobProgressReader(nodes.Progress, harness.JobStore)
            .ReadAsync(job.JobId, CancellationToken.None))!;
        progress.Status.Should().Be(OperationStatus.Cancelled);
        progress.DownloadUrl.Should().BeNull();
        await nodes.Storage.Received().DeleteAsync("export-file", Arg.Any<CancellationToken>());
    }

    [IntegrationTest]
    public async Task Export_ConcurrentSubmissionsAcrossNodes_AdmitsOnlyCapacity()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        var submissions = Enumerable.Range(0, 20).Select(async _ =>
        {
            var service = new ExportJobService(progress, NullLogger<ExportJobService>.Instance,
                harness.CreateNodeJobStore(), harness.Queue);
            try
            {
                await service.StartAsync(ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N")));
                return true;
            }
            catch (InvalidOperationException exception) when (exception.Message == ExportJobService.QueueFullMessage)
            {
                return false;
            }
        });
        var accepted = await Task.WhenAll(submissions);
        accepted.Count(value => value).Should().Be(ExportJobService.MaxOutstandingExports);
        (await harness.JobStore.ListActiveAsync(ExecutionJobKind.Export)).Should().HaveCount(ExportJobService.MaxOutstandingExports);
    }

    [IntegrationTest]
    public async Task Export_StatusReadAfterMissedTerminalCallback_ReflectsDurableCancellation()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        using var nodes = new ExportNodes(progress);
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        (await harness.JobStore.TrySetAsync(record with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        })).Should().BeTrue();

        // A different node serves this request; the worker's projection callback never ran.
        var reader = new ExecutionJobProgressReader(progress, harness.CreateNodeJobStore());
        var response = (ExportProgress)(await reader.ReadAsync(job.JobId, CancellationToken.None))!;
        response.Status.Should().Be(OperationStatus.Cancelled);
        response.CompletedAt.Should().NotBeNull();
        response.DownloadUrl.Should().BeNull();
        (await progress.GetProgressAsync<ExportProgress>(job.JobId))!.Status.Should().Be(OperationStatus.Queued,
            "serving the response must not overwrite progress belonging to a concurrent operator retry");
    }

    [IntegrationTest]
    public async Task Export_StatusReadBeforeRetryPickup_HidesPreviousAttemptCompletion()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(ExportJobTests));
        var progress = new ComparingInMemoryProgressStore();
        using var nodes = new ExportNodes(progress);
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await nodes.SubmitAsync(harness, job);
        await progress.SetProgressAsync(job.JobId,
            ExportProgress.CreateInitial(job.JobId, "csv", "svc", 1, 10) with
            {
                Status = OperationStatus.Completed,
                ProcessedFeatures = 10,
                CompletedAt = DateTimeOffset.UtcNow,
                DownloadUrl = "https://example.test/deleted.csv",
                CurrentPhase = "Completed"
            });
        var record = (await harness.JobStore.GetAsync(job.JobId))!;
        (await harness.JobStore.TrySetAsync(record with { Status = ExecutionJobStatus.Cancelled })).Should().BeTrue();
        record = (await harness.JobStore.GetAsync(job.JobId))!;
        (await harness.JobStore.TryRetryWithinCapacityAsync(record with { Status = ExecutionJobStatus.Queued }, 5))
            .Should().BeTrue();

        var reader = new ExecutionJobProgressReader(progress, harness.CreateNodeJobStore());
        var response = (ExportProgress)(await reader.ReadAsync(job.JobId, CancellationToken.None))!;
        AssertFreshAttemptResponse(response, OperationStatus.Queued);
        (await progress.GetProgressAsync<ExportProgress>(job.JobId))!.Status.Should().Be(OperationStatus.Completed,
            "response projection must not overwrite another attempt's progress");
        foreach (var status in new[] { ExecutionJobStatus.Provisioning, ExecutionJobStatus.Running })
        {
            record = (await harness.JobStore.GetAsync(job.JobId))!;
            (await harness.JobStore.TrySetAsync(record with { Status = status })).Should().BeTrue();
            response = (ExportProgress)(await reader.ReadAsync(job.JobId, CancellationToken.None))!;
            AssertFreshAttemptResponse(response, OperationStatus.Processing);
        }
    }

    private static void AssertFreshAttemptResponse(ExportProgress response, OperationStatus expected)
    {
        response.Status.Should().Be(expected);
        response.CurrentPhase.Should().Be(expected.ToString());
        response.CompletedAt.Should().BeNull();
        response.DownloadUrl.Should().BeNull();
        response.PercentComplete.Should().Be(0);
    }

    private static bool IsEnded(ExecutionJobRecord record)
        => record.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled;

    // The services every node shares: one layer, one object store, one progress store.
    private sealed class ExportNodes : IDisposable
    {
        private readonly TaskCompletionSource _firstReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ServiceProvider _services;
        private int _streams;

        public ExportNodes(ComparingInMemoryProgressStore progress)
        {
            Progress = progress;
            var store = Substitute.For<IStreamingFeatureStore>();
            store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => Read(call.ArgAt<CancellationToken>(2)));
            Storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
                .Returns(ExportJobTestData.Uploaded("export-file", "text/csv"));
            Storage.GetPresignedUrlAsync("export-file", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
                .Returns("https://example.test/export.csv");
            _services = new ServiceCollection()
                .AddSingleton(store)
                .AddSingleton(Storage)
                .AddSingleton<ICrsRegistry>(new ExportJobTestData.NullCrsRegistry())
                .BuildServiceProvider();
            Terminal = new ExportJobTerminalCallback(NullLogger<ExportJobTerminalCallback>.Instance,
                _services.GetRequiredService<IServiceScopeFactory>());
        }

        public ComparingInMemoryProgressStore Progress { get; }

        public ExportJobTerminalCallback Terminal { get; }

        public ICloudFileStorage Storage { get; } = Substitute.For<ICloudFileStorage>();

        // The first read blocks until its worker is cancelled, standing in for a node that stopped mid-export.
        public bool HoldFirstRead { get; init; }

        public Task FirstReadStarted => _firstReadStarted.Task;

        public int Streams => Volatile.Read(ref _streams);

        public JobSecurityContext? ObservedSubmitter { get; private set; }

        public Task<string> SubmitAsync(PostgresJobSubstrateHarness harness, ExportJob job)
            => new ExportJobService(Progress, NullLogger<ExportJobService>.Instance, harness.JobStore, harness.Queue)
                .StartAsync(job);

        public ExportJobExecutor CreateExecutor()
            => new(Progress, _services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportJobExecutor>.Instance);

        public void Dispose() => _services.Dispose();

        private async IAsyncEnumerable<Feature> Read([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ObservedSubmitter = JobSecurityScope.Current?.Submitter;
            if (Interlocked.Increment(ref _streams) == 1 && HoldFirstRead)
            {
                _firstReadStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            await foreach (var feature in ExportJobTestData.CreateFeatures().WithCancellation(cancellationToken))
            {
                yield return feature;
            }
        }
    }
}
