// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Io.Export;
using Honua.Server.Features.Admin.TileOperations;
using Honua.Server.Tests.Features.Export;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// A producer creates its job and then enqueues it in a second call. When that second call is lost,
// the next reconciliation sweep queues the accepted job instead of leaving it Queued forever.
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class LocalDispatchRepairTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task AcceptedExportThatWasNeverQueued_IsQueuedByTheNextSweepAndRuns()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(LocalDispatchRepairTests));
        var progressStore = new ComparingInMemoryProgressStore();
        var job = ExportJobTestData.CreateJob(Guid.NewGuid().ToString("N"));
        await new ExportJobService(progressStore, NullLogger<ExportJobService>.Instance, harness.JobStore, FailingQueue())
            .StartAsync(job);
        (await harness.IsInQueueAsync(job.JobId)).Should().BeFalse();

        await harness.RunReconciliationSweepAsync(CancellationToken.None);

        (await harness.IsInQueueAsync(job.JobId)).Should().BeTrue("the sweep queues an accepted export that lost its enqueue");
        using var services = ExportServices();
        var worker = harness.CreateWorker(
            new ExportJobExecutor(progressStore, services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ExportJobExecutor>.Instance),
            new ExportJobTerminalCallback(NullLogger<ExportJobTerminalCallback>.Instance));
        await worker.StartAsync(CancellationToken.None);
        try
        {
            var finished = await harness.WaitForJobAsync(
                job.JobId,
                record => ExecutionJobCancellationHelper.IsTerminal(record.Status),
                TimeSpan.FromSeconds(60));
            finished.Status.Should().Be(ExecutionJobStatus.Succeeded, finished.ErrorMessage);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [IntegrationTest]
    public async Task AcceptedTileOperationThatWasNeverQueued_IsQueuedByTheNextSweep()
    {
        await using var harness = await PostgresJobSubstrateHarness.CreateAsync(database, nameof(LocalDispatchRepairTests));
        var service = new TileCacheJobService(
            harness.JobStore,
            new ComparingInMemoryProgressStore(),
            [],
            StaticBatchOptions(),
            GraphScopeFactory(),
            NullLogger<TileCacheJobService>.Instance,
            FailingQueue());

        var jobId = await service.SubmitAsync(new TileOperationSubmission(
            new TileOperationStartRequest { Operation = "seed", LayerId = 1, MinZoom = 0, MaxZoom = 0 },
            "admin-a"));
        await harness.RunReconciliationSweepAsync(CancellationToken.None);

        (await harness.JobStore.GetAsync(jobId))!.Status.Should().Be(ExecutionJobStatus.Queued);
        (await harness.IsInQueueAsync(jobId)).Should().BeTrue("the sweep queues an accepted tile operation that lost its enqueue");
    }

    private static IJobQueue FailingQueue()
    {
        var queue = Substitute.For<IJobQueue>();
        queue.EnqueueAsync(Arg.Any<string>(), Arg.Any<OperationPriority>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("Simulated lost enqueue.")));
        return queue;
    }

    private static ServiceProvider ExportServices()
    {
        var store = Substitute.For<IStreamingFeatureStore>();
        store.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ExportJobTestData.CreateFeatures());
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(ExportJobTestData.Uploaded("repaired", "text/csv"));
        return new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(storage)
            .AddSingleton<ICrsRegistry>(new ExportJobTestData.NullCrsRegistry())
            .BuildServiceProvider();
    }

    private static IOptionsMonitor<TileCacheBatchOptions> StaticBatchOptions()
    {
        var options = Substitute.For<IOptionsMonitor<TileCacheBatchOptions>>();
        options.CurrentValue.Returns(new TileCacheBatchOptions());
        return options;
    }

    private static IServiceScopeFactory GraphScopeFactory()
        => new ServiceCollection()
            .AddScoped(_ => Substitute.For<IMetadataV2GraphProvider>())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();
}
