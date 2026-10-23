// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Migration;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Import;

[Collection("Unit")]
public sealed class ImportCoordinatorRecoveryTests
{
    [UnitTest]
    public async Task UnavailableCoordinator_ProbesAllStoresEvenWhenAnotherNodeIsLeader()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var manager = new RecoveringManager();
        ConfigureRecovery(manager);
        manager.LeaderElection.TryAcquireLeadershipAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return Task.FromResult(false);
        });

        await RunAsync(manager, stop, (_, _) => Task.CompletedTask);

        manager.CanAcceptNewJobs.Should().BeTrue();
        await manager.JobQueue.Received(1).GetQueueLengthAsync(Arg.Any<CancellationToken>());
        await ((IProgressStoreRecovery)manager.RequestStore).Received(1).ProbeRecoveryAsync(Arg.Any<CancellationToken>());
        await manager.RequestStore.DidNotReceive().GetActiveJobIdsAsync(Arg.Any<CancellationToken>());
        await ((IProgressStoreRecovery)manager.ProgressStore).Received(1).ProbeRecoveryAsync(Arg.Any<CancellationToken>());
        await manager.ProgressStore.DidNotReceive().GetActiveJobIdsAsync(Arg.Any<CancellationToken>());
        await manager.JobQueue.DidNotReceive().DequeueAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await manager.JobQueue.DidNotReceive().RecoverInFlightAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task RecoveredCoordinator_ResumesFencedProcessingWithoutRestartOrDuplicateRecovery()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var manager = new RecoveringManager();
        ConfigureRecovery(manager);
        manager.LeaderElection.TryAcquireLeadershipAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        manager.LeaderElection.HeartbeatAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        manager.LeaderElection.IsLeader.Returns(true);
        manager.JobQueue.DequeueAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<string?>("pending-job"));
        var processed = new List<string>();

        await RunAsync(manager, stop, (jobId, _) =>
        {
            manager.CanAcceptNewJobs.Should().BeTrue();
            processed.Add(jobId);
            stop.Cancel();
            return Task.CompletedTask;
        });

        processed.Should().ContainSingle().Which.Should().Be("pending-job");
        await manager.JobQueue.Received(1).RecoverInFlightAsync(Arg.Any<CancellationToken>());
        await manager.JobQueue.Received(1).DequeueAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await manager.LeaderElection.Received(1).ReleaseLeadershipAsync(CancellationToken.None);
    }

    [UnitTest]
    public async Task QueueRecoveredButRequestStoreUnavailable_DoesNotDequeueOrRecoverJobs()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var manager = new RecoveringManager();
        ConfigureRecovery(manager, requestStoreRecovers: false);
        manager.LeaderElection.TryAcquireLeadershipAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));
        manager.LeaderElection.HeartbeatAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            stop.Cancel();
            return Task.FromResult(true);
        });

        await RunAsync(manager, stop, (_, _) => throw new InvalidOperationException("Unhealthy coordinator must not process jobs"));

        manager.CanAcceptNewJobs.Should().BeFalse();
        await ((IProgressStoreRecovery)manager.ProgressStore).Received(1).ProbeRecoveryAsync(Arg.Any<CancellationToken>());
        await manager.ProgressStore.DidNotReceive().GetActiveJobIdsAsync(Arg.Any<CancellationToken>());
        await manager.JobQueue.DidNotReceive().DequeueAsync(Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await manager.JobQueue.DidNotReceive().RecoverInFlightAsync(Arg.Any<CancellationToken>());
    }

    private static Task RunAsync(RecoveringManager manager, CancellationTokenSource stop,
        Func<string, CancellationToken, Task> process) =>
        ImportBackgroundServiceCoordinator.RunAsync(manager, NullLogger.Instance,
            TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), process,
            (_, _) => { }, (_, _) => { }, (_, _) => { }, (_, _) => { }, stop.Token);

    private static void ConfigureRecovery(RecoveringManager manager, bool requestStoreRecovers = true)
    {
        var queueHealthy = false;
        var requestHealthy = false;
        var progressHealthy = false;
        manager.IsHealthy = () => queueHealthy && requestHealthy && progressHealthy;
        manager.JobQueue.GetQueueLengthAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            queueHealthy = true;
            return Task.FromResult(0L);
        });
        ((IProgressStoreRecovery)manager.RequestStore).ProbeRecoveryAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            requestHealthy = requestStoreRecovers;
            return Task.CompletedTask;
        });
        ((IProgressStoreRecovery)manager.ProgressStore).ProbeRecoveryAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            progressHealthy = true;
            return Task.CompletedTask;
        });
    }

    private sealed class RecoveringManager : IImportWorkerJobManager<object, object>
    {
        public IDistributedJobQueueService JobQueue { get; } = Substitute.For<IDistributedJobQueueService>();
        public IDistributedLeaderElection LeaderElection { get; } = Substitute.For<IDistributedLeaderElection>();
        public IDistributedProgressStore<object> RequestStore { get; } = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();
        public IDistributedProgressStore<object> ProgressStore { get; } = Substitute.For<IDistributedProgressStore<object>, IProgressStoreRecovery>();
        public Func<bool> IsHealthy { get; set; } = () => false;
        public bool CanAcceptNewJobs => IsHealthy();
    }
}
