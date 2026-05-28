// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Honua.Server.Tests.Helpers;
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
        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: operationId, priority: OperationPriority.Critical));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(storeFailure));

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
    public async Task TryClaimAsync_WhenStoreCasConflictAndJobTerminal_ClearsClaimAndReturnsNull()
    {
        const string operationId = "job-terminal-conflict";

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
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var queued = CreateQueuedJob(operationId: operationId, priority: OperationPriority.High);
        var cancelled = queued with
        {
            Status = ExecutionJobStatus.Cancelled,
            CompletedAt = DateTimeOffset.UtcNow
        };

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(queued, cancelled);
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var result = await queue.TryClaimAsync("worker-1");

        Assert.Null(result);
        await database.Received(1).SortedSetRemoveAsync((RedisKey)ClaimedSetKey, (RedisValue)operationId);
        await database.Received(1).KeyDeleteAsync((RedisKey)GetClaimMetaKey(operationId));
        Assert.False(HasCall(database, nameof(IDatabase.SortedSetAddAsync), (RedisKey)QueueKey, (RedisValue)operationId));
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenAllEntriesDelayed_RespectsTraverseBudgetAndTerminates()
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

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var result = await queue.TryClaimAsync("worker-traverse-budget");

        Assert.Null(result);

        var rangeCallCount = database.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IDatabase.SortedSetRangeByRankAsync));
        Assert.True(rangeCallCount <= 5000, $"Traverse budget should cap iteration; got {rangeCallCount} range calls");
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenDelayedEntriesExceedOldVisitBudget_StillClaimsReadyJob()
    {
        const int delayedCount = 1100;
        const string readyJobId = "ready-job-behind-delayed";

        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var start = (long)callInfo[1];
                var stop = (long)callInfo[2];
                var results = new List<RedisValue>();

                for (var i = start; i <= stop && i <= delayedCount; i++)
                {
                    results.Add(i < delayedCount ? $"delayed-{i}" : readyJobId);
                }

                return Task.FromResult(results.ToArray());
            });

        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var key = ((RedisKey)callInfo[0]).ToString();
                if (key.Contains("delayed-"))
                {
                    return Task.FromResult(new[] { new HashEntry("visibleAfter", futureMs) });
                }

                return Task.FromResult(Array.Empty<HashEntry>());
            });

        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(readyJobId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: readyJobId));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var result = await queue.TryClaimAsync("worker-1");

        Assert.Equal(readyJobId, result);
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenReadyJobBeyondTraverseBudget_FoundOnSubsequentPollViaRotatingCursor()
    {
        const string readyJobId = "ready-job-beyond-budget";
        const int totalDelayed = 5500;

        var database = Substitute.For<IDatabase>();
        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var start = (long)callInfo[1];
                var stop = (long)callInfo[2];
                var results = new List<RedisValue>();

                for (var i = start; i <= stop; i++)
                {
                    if (i < totalDelayed)
                        results.Add($"delayed-{i}");
                    else if (i == totalDelayed)
                        results.Add(readyJobId);
                }

                return Task.FromResult(results.ToArray());
            });

        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var key = ((RedisKey)callInfo[0]).ToString();
                if (key.Contains("delayed-"))
                    return Task.FromResult(new[] { new HashEntry("visibleAfter", futureMs) });
                return Task.FromResult(Array.Empty<HashEntry>());
            });

        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(readyJobId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: readyJobId));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var firstResult = await queue.TryClaimAsync("worker-1");
        Assert.Null(firstResult);

        var secondResult = await queue.TryClaimAsync("worker-1");
        Assert.Equal(readyJobId, secondResult);
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenQueueDrainedFromCursorPosition_CursorResetsToStart()
    {
        const string readyJobId = "ready-at-start";
        var callCount = 0;

        var database = Substitute.For<IDatabase>();
        var futureMs = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);

        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var start = (long)callInfo[1];
                var currentCall = Interlocked.Increment(ref callCount);

                if (currentCall <= 500)
                {
                    return Task.FromResult(Enumerable.Range((int)start, 10)
                        .Select(i => (RedisValue)$"delayed-{i}").ToArray());
                }

                if (currentCall == 501)
                {
                    return Task.FromResult(Array.Empty<RedisValue>());
                }

                if (start == 0 && currentCall == 502)
                {
                    return Task.FromResult(new RedisValue[] { readyJobId });
                }

                return Task.FromResult(Array.Empty<RedisValue>());
            });

        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var key = ((RedisKey)callInfo[0]).ToString();
                if (key.Contains("delayed-"))
                    return Task.FromResult(new[] { new HashEntry("visibleAfter", futureMs) });
                return Task.FromResult(Array.Empty<HashEntry>());
            });

        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(readyJobId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: readyJobId));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var firstResult = await queue.TryClaimAsync("worker-1");
        Assert.Null(firstResult);

        var secondResult = await queue.TryClaimAsync("worker-1");
        Assert.Null(secondResult);

        var thirdResult = await queue.TryClaimAsync("worker-1");
        Assert.Equal(readyJobId, thirdResult);
    }

    [UnitTest]
    public async Task TryClaimAsync_WhenKindMismatchedEntriesExceedOldVisitBudget_StillClaimsReadyJob()
    {
        const int mismatchedCount = 1100;
        const string readyJobId = "ready-job-behind-mismatched";
        var acceptedKinds = new HashSet<ExecutionJobKind> { ExecutionJobKind.ExtractTransformLoad };

        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var start = (long)callInfo[1];
                var stop = (long)callInfo[2];
                var results = new List<RedisValue>();

                for (var i = start; i <= stop && i <= mismatchedCount; i++)
                {
                    results.Add(i < mismatchedCount ? $"mismatched-{i}" : readyJobId);
                }

                return Task.FromResult(results.ToArray());
            });

        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<HashEntry>()));
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(Arg.Is<string>(id => id.StartsWith("mismatched-")), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var id = (string)callInfo[0];
                return Task.FromResult<ExecutionJobRecord?>(
                    CreateQueuedJob(operationId: id, kind: ExecutionJobKind.Geoprocessing));
            });
        jobStore.GetAsync(readyJobId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(operationId: readyJobId, kind: ExecutionJobKind.ExtractTransformLoad));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var queue = new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);

        var result = await queue.TryClaimAsync("worker-1", acceptedKinds);

        Assert.Equal(readyJobId, result);
    }

    private static ExecutionJobRecord CreateQueuedJob(
        string operationId = "job-1",
        OperationPriority priority = OperationPriority.Normal,
        DateTimeOffset? nextRetryAt = null,
        ExecutionJobKind kind = ExecutionJobKind.Geoprocessing,
        string? runtimeProfile = null)
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
                Kind = kind,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "test",
                RuntimeProfile = runtimeProfile
            }
        };
    }

    /// <summary>
    /// Builds a substituted Redis + job store so a single queued job (with the given
    /// runtime profile) can be driven through the real <see cref="RedisJobQueue"/>
    /// claim path without Redis/Docker.
    /// </summary>
    private static RedisJobQueue CreateQueueWithSingleJob(string operationId, string? runtimeProfile)
    {
        var database = Substitute.For<IDatabase>();
        database.SortedSetRangeByRankAsync(
                Arg.Any<RedisKey>(),
                Arg.Any<long>(),
                Arg.Any<long>(),
                Arg.Any<Order>(),
                Arg.Any<CommandFlags>())
            .Returns(callInfo =>
            {
                var start = (long)callInfo[1];
                // Single entry at rank 0; any non-zero offset is past the end.
                return Task.FromResult(start == 0
                    ? new[] { (RedisValue)operationId }
                    : Array.Empty<RedisValue>());
            });
        database.HashGetAllAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(Array.Empty<HashEntry>()));
        database.HashSetAsync(Arg.Any<RedisKey>(), Arg.Any<HashEntry[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.CompletedTask);
        database.ScriptEvaluateAsync(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]>(),
                Arg.Any<RedisValue[]>(),
                Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)"1")));

        var redis = Substitute.For<IConnectionMultiplexer>();
        redis.GetDatabase(Arg.Any<int>(), Arg.Any<object>()).Returns(database);

        var jobStore = Substitute.For<IExecutionJobStore>().WithTrySet();
        jobStore.GetAsync(operationId, Arg.Any<CancellationToken>())
            .Returns(CreateQueuedJob(
                operationId: operationId,
                kind: ExecutionJobKind.ExtractTransformLoad,
                runtimeProfile: runtimeProfile));
        jobStore.TrySetAsync(Arg.Any<ExecutionJobRecord>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        return new RedisJobQueue(redis, jobStore, NullLogger<RedisJobQueue>.Instance);
    }

    // ---- Runtime-profile claim fence invariant ----
    // A worker whose accepted set is null/managed-only must NOT claim a native job;
    // it MUST claim a managed/null job. A worker accepting { "native" } claims the
    // native job but NOT the managed one. See RuntimeProfiles.CanClaim.

    [UnitTest]
    public async Task TryClaimAsync_LeanWorker_DoesNotClaimNativeJob()
    {
        var queue = CreateQueueWithSingleJob("native-job", RuntimeProfiles.Native);

        // Null accepted-profiles => managed/default only (the lean worker).
        var result = await queue.TryClaimAsync("worker-lean", acceptedRuntimeProfiles: null);

        Assert.Null(result);
    }

    [UnitTest]
    public async Task TryClaimAsync_LeanWorker_WithExplicitManagedSet_DoesNotClaimNativeJob()
    {
        var queue = CreateQueueWithSingleJob("native-job", RuntimeProfiles.Native);

        var result = await queue.TryClaimAsync(
            "worker-lean",
            acceptedRuntimeProfiles: RuntimeProfiles.DefaultAccepted);

        Assert.Null(result);
    }

    [UnitTest]
    public async Task TryClaimAsync_LeanWorker_ClaimsManagedJob()
    {
        var queue = CreateQueueWithSingleJob("managed-job", runtimeProfile: RuntimeProfiles.Managed);

        var result = await queue.TryClaimAsync("worker-lean", acceptedRuntimeProfiles: null);

        Assert.Equal("managed-job", result);
    }

    [UnitTest]
    public async Task TryClaimAsync_LeanWorker_ClaimsNullProfileJobAsManaged()
    {
        var queue = CreateQueueWithSingleJob("legacy-job", runtimeProfile: null);

        var result = await queue.TryClaimAsync("worker-lean", acceptedRuntimeProfiles: null);

        Assert.Equal("legacy-job", result);
    }

    [UnitTest]
    public async Task TryClaimAsync_NativeWorker_ClaimsNativeJob()
    {
        var queue = CreateQueueWithSingleJob("native-job", RuntimeProfiles.Native);
        var nativeAccepted = new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

        var result = await queue.TryClaimAsync("worker-native", acceptedRuntimeProfiles: nativeAccepted);

        Assert.Equal("native-job", result);
    }

    [UnitTest]
    public async Task TryClaimAsync_NativeWorker_DoesNotClaimManagedJob()
    {
        var queue = CreateQueueWithSingleJob("managed-job", runtimeProfile: null);
        var nativeAccepted = new HashSet<string>(StringComparer.Ordinal) { RuntimeProfiles.Native };

        var result = await queue.TryClaimAsync("worker-native", acceptedRuntimeProfiles: nativeAccepted);

        Assert.Null(result);
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
