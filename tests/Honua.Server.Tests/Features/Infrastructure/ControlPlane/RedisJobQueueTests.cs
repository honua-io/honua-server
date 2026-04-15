// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

/// <summary>
/// Verifies that Redis job-queue claim repair stays atomic even when recovery
/// throws, so queued jobs remain discoverable by either the pending set or the
/// stale-claim reconciler.
/// </summary>
[Collection("Unit")]
public sealed class RedisJobQueueTests
{
    private const string QueueKey = "controlplane:jobqueue:pending";
    private const string ClaimedSetKey = "controlplane:jobqueue:claimed";

    [UnitTest]
    public async Task ReconcileStaleClaimsAsync_WhenAtomicRepairThrows_DoesNotFallbackToMultiStepMove()
    {
        const string operationId = "job-stale-queued";
        var repairFailure = new RedisConnectionException(
            ConnectionFailureType.SocketFailure,
            "simulated stale-claim repair failure");

        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByScoreAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<double>(),
                Arg.Any<double>(),
                Arg.Any<Exclude>(),
                Arg.Any<Order>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { operationId }));
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromException<RedisResult>(repairFailure));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var nextRetryAt = DateTimeOffset.UtcNow.AddMinutes(2);
        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(
                operationId: operationId,
                priority: OperationPriority.High,
                nextRetryAt: nextRetryAt));

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var exception = await Assert.ThrowsAsync<RedisConnectionException>(() =>
            queue.ReconcileStaleClaimsAsync(TimeSpan.FromSeconds(60)));

        Assert.Same(repairFailure, exception);
        Assert.Equal(1, CountCalls(database, nameof(IDatabase.ScriptEvaluateAsync)));
        Assert.False(HasCall(database, nameof(IDatabase.SortedSetRemoveAsync), (RedisKey)ClaimedSetKey, (RedisValue)operationId));
        Assert.False(HasCall(database, nameof(IDatabase.SortedSetAddAsync), (RedisKey)QueueKey, (RedisValue)operationId));
        Assert.False(HasCall(database, nameof(IDatabase.KeyDeleteAsync), (RedisKey)GetClaimMetaKey(operationId)));
        Assert.False(HasCall(database, nameof(IDatabase.HashSetAsync), (RedisKey)GetClaimMetaKey(operationId)));
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenRollbackRepairThrows_DoesNotFallbackToMultiStepMove()
    {
        const string operationId = "job-rollback";
        var storeFailure = new InvalidOperationException("simulated store write failure");
        var rollbackFailure = new RedisConnectionException(
            ConnectionFailureType.SocketFailure,
            "simulated rollback failure");

        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { operationId }));
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<HashEntry>()));
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(
                Task.FromResult(RedisResult.Create((RedisValue)"1")),
                Task.FromException<RedisResult>(rollbackFailure));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>();
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: operationId, priority: OperationPriority.Critical));
        jobStore.SetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(storeFailure));

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            queue.TryClaimAsync("worker-1"));

        Assert.Same(storeFailure, exception);
        Assert.Equal(2, CountCalls(database, nameof(IDatabase.ScriptEvaluateAsync)));
        Assert.False(HasCall(database, nameof(IDatabase.SortedSetRemoveAsync), (RedisKey)ClaimedSetKey, (RedisValue)operationId));
        Assert.False(HasCall(database, nameof(IDatabase.SortedSetAddAsync), (RedisKey)QueueKey, (RedisValue)operationId));
        Assert.False(HasCall(database, nameof(IDatabase.KeyDeleteAsync), (RedisKey)GetClaimMetaKey(operationId)));
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenAllEntriesDelayed_RespectsVisitBudgetAndTerminates()
    {
        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new RedisValue[] { "delayed-job" }));

        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(new[] { new HashEntry("visibleAfter", futureMs) }));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>();
        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var result = await queue.TryClaimAsync("worker-visit-budget");

        Assert.Null(result);

        var rangeCallCount = database.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IDatabase.SortedSetRangeByRankAsync));
        Assert.True(rangeCallCount <= 1000, $"Visit budget should cap iteration; got {rangeCallCount} range calls");
    }

    private static ExecutionJobRecord CreateQueuedJob(
        string operationId = "job-1",
        OperationPriority priority = OperationPriority.Normal,
        DateTimeOffset? nextRetryAt = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExecutionJobRecord
        {
            OperationId = operationId,
            Status = ExecutionJobStatus.Queued,
            Priority = priority,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-1),
            NextRetryAt = nextRetryAt,
            RetryPolicy = JobRetryPolicy.Default,
            HeartbeatPolicy = JobHeartbeatPolicy.Default,
            TimeoutPolicy = JobTimeoutPolicy.Default,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test"
            }
        };
    }

    private static int CountCalls(IDatabase database, string methodName)
        => database.ReceivedCalls().Count(call => call.GetMethodInfo().Name == methodName);

    private static bool HasCall(IDatabase database, string methodName, params object[] expectedPrefix)
        => database.ReceivedCalls().Any(call =>
        {
            if (call.GetMethodInfo().Name != methodName)
            {
                return false;
            }

            var args = call.GetArguments();
            if (args.Length < expectedPrefix.Length)
            {
                return false;
            }

            for (var index = 0; index < expectedPrefix.Length; index++)
            {
                if (!Equals(args[index], expectedPrefix[index]))
                {
                    return false;
                }
            }

            return true;
        });

    private static string GetClaimMetaKey(string operationId)
        => $"controlplane:jobqueue:meta:{operationId}";
}
