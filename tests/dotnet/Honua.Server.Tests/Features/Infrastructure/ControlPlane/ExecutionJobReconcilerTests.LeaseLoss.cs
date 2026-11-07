// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

public sealed partial class ExecutionJobReconcilerTests
{
    public enum LeaseRenewalFailure
    {
        RedisUnavailable,
        TimedOut,
        OwnershipLost
    }

    [UnitTheory]
    [InlineData(LeaseRenewalFailure.RedisUnavailable)]
    [InlineData(LeaseRenewalFailure.TimedOut)]
    [InlineData(LeaseRenewalFailure.OwnershipLost)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReconcileExecutionJob_LeaseRenewalFails_CancelsObservationAndReleasesLease(LeaseRenewalFailure failure)
    {
        var job = CreateJobRecord("renewal-loss", ExecutionJobStatus.Running, "lease-test", BatchComputeTargetKind.AwsBatch);
        var store = LeaseStore(job);
        var renewal = failure switch
        {
            LeaseRenewalFailure.RedisUnavailable => Task.FromException<bool>(LeaseUnavailable()),
            LeaseRenewalFailure.TimedOut => Task.FromException<bool>(new TimeoutException("Lease renewal timed out")),
            _ => Task.FromResult(false)
        };
        store.RenewLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(renewal);
        var observationCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("lease-test");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);
        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            using var registration = token.Register(() => observationCancelled.TrySetResult());
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new BatchComputeObservation { Status = ExecutionJobStatus.Running };
        });
        using var shutdown = new CancellationTokenSource();
        var reconciliation = LeaseReconciler(store, backend).ReconcileExecutionJobAsync(job.OperationId, shutdown.Token);
        try
        {
            await observationCancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));

            await store.Received(1).ReleaseLeaseAsync(job.OperationId, Arg.Any<string>(),
                Arg.Is<CancellationToken>(token => token.CanBeCanceled && !token.IsCancellationRequested));
            await store.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
            Assert.Equal(ExecutionJobStatus.Running, (await store.GetAsync(job.OperationId))!.Status);
        }
        finally
        {
            shutdown.Cancel();
            try
            {
                await reconciliation.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Cleanup must not replace the ownership-cancellation assertion with the renewal fault.
            }
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReconcileExecutionJob_LeaseReleaseFails_PreservesTerminalJob()
    {
        var job = CreateJobRecord("release-unavailable", ExecutionJobStatus.Succeeded, "lease-test", BatchComputeTargetKind.AwsBatch);
        var store = LeaseStore(job);
        store.ReleaseLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(LeaseUnavailable()));

        await LeaseReconciler(store).ReconcileExecutionJobAsync(job.OperationId);

        Assert.Equal(ExecutionJobStatus.Succeeded, (await store.GetAsync(job.OperationId))!.Status);
        await store.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReconcileExecutionJob_LeaseReleaseIgnoresCancellation_CompletesWithinCleanupBudget()
    {
        var job = CreateJobRecord("release-stalled", ExecutionJobStatus.Succeeded, "lease-test", BatchComputeTargetKind.AwsBatch);
        var store = LeaseStore(job);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.ReleaseLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(release.Task);
        var reconciliation = LeaseReconciler(store).ReconcileExecutionJobAsync(job.OperationId);
        try
        {
            await reconciliation.WaitAsync(TimeSpan.FromSeconds(7));
        }
        finally
        {
            release.TrySetResult();
            await reconciliation;
        }
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReconcileExecutionJob_LeaseRenewalIgnoresCancellation_StillReleasesLease()
    {
        var job = CreateJobRecord("renewal-stalled", ExecutionJobStatus.Running, "lease-test", BatchComputeTargetKind.AwsBatch);
        var store = LeaseStore(job);
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.RenewLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                renewalStarted.TrySetResult();
                return renewal.Task;
            });
        var backend = Substitute.For<IBatchComputeBackend>();
        backend.BackendName.Returns("lease-test");
        backend.TargetKind.Returns(BatchComputeTargetKind.AwsBatch);

        async Task<BatchComputeObservation> FailAfterLeaseRenewalStartsAsync()
        {
            await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            throw LeaseUnavailable();
        }

        backend.ObserveAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailAfterLeaseRenewalStartsAsync());
        var reconciliation = LeaseReconciler(store, backend).ReconcileExecutionJobAsync(job.OperationId);
        try
        {
            var exception = await Assert.ThrowsAsync<RedisConnectionException>(
                () => reconciliation.WaitAsync(TimeSpan.FromSeconds(22)));
            Assert.Equal("Lease store unavailable", exception.Message);
            await store.Received(1).ReleaseLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<CancellationToken>());
            await store.DidNotReceive().TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            renewal.TrySetResult(false);
        }
    }

    private static IExecutionJobStore LeaseStore(ExecutionJobRecord job)
    {
        var store = Substitute.For<IExecutionJobStore>();
        store.GetAsync(job.OperationId, Arg.Any<CancellationToken>()).Returns(job);
        store.TryAcquireLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        store.RenewLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns(true);
        store.ReleaseLeaseAsync(job.OperationId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return store;
    }

    private static ExecutionJobReconciler LeaseReconciler(IExecutionJobStore store, params IBatchComputeBackend[] backends)
        => new(store, backends, new InMemoryProgressStore(), NullLogger<ExecutionJobReconciler>.Instance);

    private static RedisConnectionException LeaseUnavailable()
        => new(ConnectionFailureType.UnableToConnect, "Lease store unavailable");
}
