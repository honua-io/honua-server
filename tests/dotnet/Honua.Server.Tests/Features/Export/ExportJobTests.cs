// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
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
        var progressStore = new ComparingInMemoryProgressStore();
        var streams = 0;
        JobSecurityContext? observedSubmitter = null;
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Interlocked.Increment(ref streams);
                observedSubmitter = JobSecurityScope.Current?.Submitter;
                return ExportJobTestData.CreateFeatures();
            });
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExportJobTestData.Uploaded("export-file", "text/csv"));
        storage.GetPresignedUrlAsync("export-file", Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns("https://example.test/export.csv");
        using var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(storage)
            .AddSingleton<ICrsRegistry>(new ExportJobTestData.NullCrsRegistry())
            .BuildServiceProvider();
        var terminal = new ExportJobTerminalCallback(progressStore, NullLogger<ExportJobTerminalCallback>.Instance);
        var nodeA = harness.CreateWorker(CreateExecutor(), terminal, harness.CreateNodeJobStore());
        var nodeB = harness.CreateWorker(CreateExecutor(), terminal, harness.CreateNodeJobStore());
        await nodeA.StartAsync(CancellationToken.None);
        await nodeB.StartAsync(CancellationToken.None);

        try
        {
            var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
            await new ExportJobService(progressStore, NullLogger<ExportJobService>.Instance, harness.JobStore, harness.Queue)
                .StartAsync(job);

            var finished = await harness.WaitForJobAsync(
                job.JobId,
                record => record.Status is ExecutionJobStatus.Succeeded or ExecutionJobStatus.Failed or ExecutionJobStatus.Cancelled,
                TimeSpan.FromSeconds(60));

            // Give a duplicate claim time to surface before asserting there was none.
            await Task.Delay(TimeSpan.FromSeconds(2));

            finished.Status.Should().Be(ExecutionJobStatus.Succeeded, finished.ErrorMessage);
            finished.AttemptCount.Should().Be(1, "one node claimed the export and no other attempt ran");
            Volatile.Read(ref streams).Should().Be(1, "the layer is read once across both nodes");
            await storage.Received(1).UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
            observedSubmitter.Should().BeEquivalentTo(job.Submitter, "the worker runs the export as its submitter");
            var progress = await progressStore.GetProgressAsync<ExportProgress>(job.JobId);
            progress!.Status.Should().Be(OperationStatus.Completed);
            progress.DownloadUrl.Should().Be("https://example.test/export.csv");
            (await harness.Queue.GetQueueDepthAsync()).Should().Be(0);
        }
        finally
        {
            await nodeA.StopAsync(CancellationToken.None);
            await nodeB.StopAsync(CancellationToken.None);
        }

        ExportJobExecutor CreateExecutor()
            => new(progressStore, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportJobExecutor>.Instance);
    }
}
