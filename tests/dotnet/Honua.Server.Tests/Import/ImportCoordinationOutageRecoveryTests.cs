// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Redis outage and recovery of durable import coordination using the production
/// coordination components over isolated in-process Redis/cache doubles (#4877).
/// </summary>
[Collection("Unit")]
public sealed class ImportCoordinationOutageRecoveryTests
{
    private const string ServiceUrl = "https://example.com/arcgis/rest/services/Test/FeatureServer";
    private const string GeoServerUrl = "https://example.com/geoserver/rest";

    [UnitTest]
    public async Task GeoservicesAdmission_RefusesDuringOutageAndRecoversWithoutDummyWrite()
    {
        var coordination = OutageCoordination.ForGeoservices();
        using var manager = (RedisImportJobManager)coordination.JobManager;

        await AssertOutageAndRecoveryAsync(
            coordination,
            manager,
            GeoservicesRequest("outage-job"),
            GeoservicesImportProgress.CreateInitial("outage-job", ServiceUrl, 0, "outage_table"));
    }

    [UnitTest]
    public async Task GeoServerAdmission_RefusesDuringOutageAndRecoversWithoutDummyWrite()
    {
        var coordination = OutageCoordination.ForGeoServer();
        using var manager = (GeoServerImportJobManager)coordination.JobManager;

        await AssertOutageAndRecoveryAsync<GeoServerImportRequest, GeoServerImportProgress>(
            coordination,
            manager,
            GeoServerRequest("outage-job"),
            GeoServerImportProgress.CreateInitial("outage-job", GeoServerUrl, "https://localhost"));
    }

    [UnitTest]
    public async Task GeoservicesWorker_ThroughOutageAndRecovery_ProcessesPostRecoveryJobOnceWithoutRestart()
    {
        var coordination = OutageCoordination.ForGeoservices();
        using var manager = (RedisImportJobManager)coordination.JobManager;

        await AssertWorkerRecoveryAsync(
            coordination,
            manager,
            "geoservices:import",
            GeoservicesRequest,
            jobId => GeoservicesImportProgress.CreateInitial(jobId, ServiceUrl, 0, "recovered_table"),
            progress => progress with { Status = GeoservicesImportStatus.Completed, CompletedAt = DateTimeOffset.UtcNow });
    }

    [UnitTest]
    public async Task GeoServerWorker_ThroughOutageAndRecovery_ProcessesPostRecoveryJobOnceWithoutRestart()
    {
        var coordination = OutageCoordination.ForGeoServer();
        using var manager = (GeoServerImportJobManager)coordination.JobManager;

        await AssertWorkerRecoveryAsync<GeoServerImportRequest, GeoServerImportProgress>(
            coordination,
            manager,
            "geoserver:import",
            GeoServerRequest,
            jobId => GeoServerImportProgress.CreateInitial(jobId, GeoServerUrl, "https://localhost"),
            progress => progress with { Status = GeoServerImportStatus.Completed, CompletedAt = DateTimeOffset.UtcNow });
    }

    [UnitTest]
    public async Task ProbeAsync_WithHungQueue_BoundsTheProbeAndStillProbesTheStores()
    {
        var queue = Substitute.For<IDistributedJobQueueService>();
        queue.GetQueueLengthAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<long>().Task);
        var requestStore = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();
        var progressStore = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();
        var stopwatch = Stopwatch.StartNew();

        await ImportCoordinationRecovery.ProbeAsync(queue, requestStore, progressStore, CancellationToken.None);

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "a hung dependency must not stall recovery");
        await ((IProgressStoreRecovery)requestStore).Received(1).ProbeRecoveryAsync(Arg.Any<CancellationToken>());
        await ((IProgressStoreRecovery)progressStore).Received(1).ProbeRecoveryAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ProbeAsync_HungProbe_DoesNotStartAnotherWhileTheFirstIsRunning()
    {
        var queue = Substitute.For<IDistributedJobQueueService>();
        queue.GetQueueLengthAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<long>().Task);
        var requestStore = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();
        var progressStore = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();

        await ImportCoordinationRecovery.ProbeAsync(queue, requestStore, progressStore, CancellationToken.None);
        await ImportCoordinationRecovery.ProbeAsync(queue, requestStore, progressStore, CancellationToken.None);

        await queue.Received(1).GetQueueLengthAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task TryAdmitAsync_WhenCancelledDuringHungProbe_StopsPromptly()
    {
        var health = Substitute.For<IImportCoordinationHealth>();
        health.CanAcceptNewJobs.Returns(false);
        var queue = Substitute.For<IDistributedJobQueueService>();
        queue.GetQueueLengthAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<long>().Task);
        var store = Substitute.For<IDistributedProgressStore<object>>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var stopwatch = Stopwatch.StartNew();

        await FluentActions
            .Invoking(() => ImportCoordinationRecovery.TryAdmitAsync(health, queue, store, store, cancellation.Token))
            .Should()
            .ThrowAsync<OperationCanceledException>();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4), "probes honour the caller's cancellation");
    }

    [UnitTest]
    public async Task TryAdmitAsync_WhenHealthy_DoesNoRecoveryIo()
    {
        var health = Substitute.For<IImportCoordinationHealth>();
        health.CanAcceptNewJobs.Returns(true);
        var queue = Substitute.For<IDistributedJobQueueService>();
        var store = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();

        (await ImportCoordinationRecovery.TryAdmitAsync(health, queue, store, store, CancellationToken.None)).Should().BeTrue();

        queue.ReceivedCalls().Should().BeEmpty();
        store.ReceivedCalls().Should().BeEmpty();
    }

    private static async Task AssertOutageAndRecoveryAsync<TRequest, TProgress>(
        OutageCoordination coordination,
        IImportWorkerJobManager<TRequest, TProgress> manager,
        TRequest request,
        TProgress progress)
        where TRequest : class
        where TProgress : class
    {
        (await TryAdmitAsync(manager)).Should().BeTrue();

        coordination.BeginOutage();
        await FluentActions.Invoking(() => manager.RequestStore.SetProgressAsync("outage-job", request))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => manager.ProgressStore.SetProgressAsync("outage-job", progress))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => manager.JobQueue.EnqueueAsync("outage-job"))
            .Should().ThrowAsync<InvalidOperationException>("production never queues node-local work");
        manager.CanAcceptNewJobs.Should().BeFalse();

        coordination.ElapseRetryThrottles();
        (await TryAdmitAsync(manager)).Should().BeFalse("Redis is still unavailable");
        (await manager.LeaderElection.TryAcquireLeadershipAsync()).Should().BeFalse("production never takes node-local leadership");
        (await manager.JobQueue.DequeueAsync(TimeSpan.FromMilliseconds(20))).Should().BeNull();

        // The Redis connection answers pings again, but request/progress storage does not.
        coordination.Redis.Down = false;
        coordination.ElapseRetryThrottles();
        var pingsBefore = coordination.Redis.PingCount;
        (await TryAdmitAsync(manager)).Should().BeFalse("a successful ping must not vouch for the request and progress stores");
        coordination.Redis.PingCount.Should().BeGreaterThan(pingsBefore);

        coordination.Cache.Down = false;
        var writesBefore = coordination.Cache.WriteCount;
        (await TryAdmitAsync(manager)).Should().BeFalse("each store keeps its own retry throttle");
        coordination.ElapseRetryThrottles();
        (await TryAdmitAsync(manager)).Should().BeTrue("bounded probes restore admission without a restart");
        coordination.Cache.WriteCount.Should().Be(writesBefore, "recovery reads health keys and never needs a dummy write");

        coordination.Cache.Keys.Should().NotContain(key => key.Contains("outage-job", StringComparison.Ordinal));
        (await manager.JobQueue.GetQueueLengthAsync()).Should().Be(0, "the refused submission left no queued work behind");
    }

    private static async Task AssertWorkerRecoveryAsync<TRequest, TProgress>(
        OutageCoordination coordination,
        IImportWorkerJobManager<TRequest, TProgress> manager,
        string keyPrefix,
        Func<string, TRequest> createRequest,
        Func<string, TProgress> createProgress,
        Func<TProgress, TProgress> complete)
        where TRequest : class
        where TProgress : class
    {
        coordination.BeginOutage();
        await FluentActions.Invoking(() => manager.RequestStore.SetProgressAsync("outage-job", createRequest("outage-job")))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => manager.ProgressStore.SetProgressAsync("outage-job", createProgress("outage-job")))
            .Should().ThrowAsync<InvalidOperationException>();
        await FluentActions.Invoking(() => manager.JobQueue.EnqueueAsync("outage-job"))
            .Should().ThrowAsync<InvalidOperationException>();

        var processed = new ConcurrentQueue<string>();
        var notLeaderTicks = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var worker = ImportBackgroundServiceCoordinator.RunAsync(
            manager,
            NullLogger.Instance,
            TimeSpan.FromMilliseconds(20),
            TimeSpan.FromMilliseconds(20),
            async (jobId, token) =>
            {
                processed.Enqueue(jobId);
                var stored = await manager.ProgressStore.GetProgressAsync(jobId, token);
                await manager.ProgressStore.SetProgressAsync(jobId, complete(stored!), TimeSpan.FromHours(1), token);
                await ImportBackgroundServiceCoordinator.AcknowledgeCompletionAsync(
                    manager.JobQueue, jobId, acknowledgeCompletion: true, NullLogger.Instance, (_, _, _) => { });
            },
            (_, _) => { },
            (_, _) => { },
            (_, _) => Interlocked.Increment(ref notLeaderTicks),
            (_, _) => { },
            stop.Token);

        await WaitUntilAsync(() => Task.FromResult(Volatile.Read(ref notLeaderTicks) >= 3), stop.Token);
        processed.Should().BeEmpty("no work is processed while durable coordination is unavailable");

        coordination.EndOutage();
        await WaitUntilAsync(async () =>
        {
            coordination.ElapseRetryThrottles();
            return await TryAdmitAsync(manager);
        }, stop.Token);

        // Submit once, exactly as the start endpoints persist and enqueue a job.
        const string JobId = "recovered-job";
        await manager.RequestStore.SetProgressAsync(JobId, createRequest(JobId), TimeSpan.FromHours(1));
        await manager.ProgressStore.SetProgressAsync(JobId, createProgress(JobId), TimeSpan.FromHours(1));
        await manager.JobQueue.EnqueueAsync(JobId);

        await WaitUntilAsync(() => Task.FromResult(
            processed.Count == 1 &&
            coordination.Redis.ListContents($"{keyPrefix}:queue:processing").Count == 0), stop.Token);

        // Keep the worker polling to prove the acknowledged job is not redelivered.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await stop.CancelAsync();
        await worker;

        processed.Should().ContainSingle().Which.Should().Be(JobId);
        coordination.Redis.ListContents($"{keyPrefix}:queue").Should().BeEmpty();
        coordination.Redis.ListContents($"{keyPrefix}:queue:processing").Should().BeEmpty();
        coordination.Redis.SetContents($"{keyPrefix}:request:__active").Should().Equal(JobId);
        coordination.Cache.Keys.Should().NotContain(key => key.Contains("outage-job", StringComparison.Ordinal));
        (await manager.RequestStore.GetProgressAsync(JobId)).Should().NotBeNull();
    }

    private static Task<bool> TryAdmitAsync<TRequest, TProgress>(IImportWorkerJobManager<TRequest, TProgress> manager)
        where TRequest : class
        where TProgress : class
        => ImportCoordinationRecovery.TryAdmitAsync(
            manager,
            manager.JobQueue,
            manager.RequestStore,
            manager.ProgressStore,
            CancellationToken.None);

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken cancellationToken)
    {
        while (!await condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }
    }

    private static GeoservicesImportRequest GeoservicesRequest(string jobId) => new()
    {
        JobId = jobId,
        ServiceUrl = ServiceUrl,
        LayerId = 0,
        TableName = "recovered_table",
        AutoPublish = false
    };

    private static GeoServerImportRequest GeoServerRequest(string jobId) => new()
    {
        JobId = jobId,
        GeoServerRestUrl = GeoServerUrl,
        TargetHonuaUrl = "https://localhost",
        DryRun = true
    };
}
