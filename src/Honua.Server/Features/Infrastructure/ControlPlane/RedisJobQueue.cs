// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Detects and recovers claims that were orphaned between the atomic queue move
/// and the subsequent job store update.
/// </summary>
internal interface IQueueClaimReconciler
{
    /// <summary>
    /// Scans for stale claims older than <paramref name="staleThreshold"/> whose
    /// backing job record has not advanced past Queued, and requeues them.
    /// </summary>
    Task ReconcileStaleClaimsAsync(TimeSpan staleThreshold, CancellationToken cancellationToken);
}

/// <summary>
/// Redis-backed durable job queue with atomic claim semantics.
/// Uses a sorted set keyed by priority score for dequeue ordering and
/// per-job metadata hashes for claim tracking.
/// </summary>
internal sealed partial class RedisJobQueue(
    IConnectionMultiplexer redis,
    IExecutionJobStore jobStore,
    ILogger<RedisJobQueue> logger) : IJobQueue, IQueueClaimReconciler
{
    private const string QueueKey = "controlplane:jobqueue:pending";
    private const string ClaimedSetKey = "controlplane:jobqueue:claimed";
    private const int MaxScanEntries = 100;

    /// <summary>
    /// Lua script that atomically removes a job from the pending set and adds it
    /// to the claimed set. Returns 1 on success, 0 if another worker claimed first.
    /// KEYS[1] = pending set, KEYS[2] = claimed set.
    /// ARGV[1] = operationId, ARGV[2] = claim timestamp (ms).
    /// </summary>
    private const string AtomicClaimScript = """
        local removed = redis.call('ZREM', KEYS[1], ARGV[1])
        if removed == 1 then
            redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
            return 1
        end
        return 0
        """;

    /// <summary>
    /// Lua script that atomically removes a job from the claimed set and adds it
    /// back to the pending set. Optionally sets or clears visibility metadata.
    /// KEYS[1] = claimed set, KEYS[2] = pending set, KEYS[3] = claim meta hash.
    /// ARGV[1] = operationId, ARGV[2] = score, ARGV[3] = visibleAfterMs (empty to clear meta).
    /// </summary>
    private const string AtomicRequeueScript = """
        redis.call('ZREM', KEYS[1], ARGV[1])
        redis.call('ZADD', KEYS[2], ARGV[2], ARGV[1])
        if ARGV[3] ~= '' then
            redis.call('HSET', KEYS[3], 'visibleAfter', ARGV[3])
        else
            redis.call('DEL', KEYS[3])
        end
        return 1
        """;

    private readonly IDatabase _database = redis.GetDatabase();

    public async Task EnqueueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var score = ComputeScore(priority, DateTimeOffset.UtcNow);
        await _database.SortedSetAddAsync(QueueKey, operationId, score).ConfigureAwait(false);

        Log.JobEnqueued(logger, operationId, priority.ToString());
    }

    public async Task<string?> TryClaimAsync(
        string workerId,
        IReadOnlySet<ExecutionJobKind>? acceptedKinds = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Paginate through the sorted set to find a claimable job. Tracks removed
        // entries so offset adjustments stay correct across batches.
        const int batchSize = 10;
        var now = DateTimeOffset.UtcNow;
        long offset = 0;
        var totalScanned = 0;

        while (totalScanned < MaxScanEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = await _database.SortedSetRangeByRankAsync(
                QueueKey, offset, offset + batchSize - 1).ConfigureAwait(false);

            if (candidates.Length == 0)
            {
                break;
            }

            var removedFromSet = 0;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!candidate.HasValue)
                {
                    continue;
                }

                var operationId = candidate.ToString();

                // Filter delayed jobs (requeue with visibility delay).
                var meta = await _database.HashGetAllAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);
                var visibleAfter = GetVisibleAfter(meta);
                if (visibleAfter.HasValue && visibleAfter.Value > now)
                {
                    totalScanned++;
                    continue;
                }

                // Load the job record to check kind filter and current status.
                var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
                if (job == null || IsTerminal(job.Status))
                {
                    // Stale entry; remove from queue.
                    await _database.SortedSetRemoveAsync(QueueKey, operationId).ConfigureAwait(false);
                    removedFromSet++;
                    totalScanned++;
                    continue;
                }

                // Kind-mismatched entries do not consume the claim scan budget
                // so dedicated workers can reach their own jobs deeper in the queue.
                if (acceptedKinds != null && !acceptedKinds.Contains(job.Spec.Kind))
                {
                    continue;
                }

                totalScanned++;

                // Attempt atomic claim: remove from pending and add to claimed in a
                // single Lua evaluation to prevent the window where the job exists in
                // neither set.
                var claimResult = (int)await _database.ScriptEvaluateAsync(
                    AtomicClaimScript,
                    [(RedisKey)QueueKey, (RedisKey)ClaimedSetKey],
                    [(RedisValue)operationId, now.ToUnixTimeMilliseconds()]).ConfigureAwait(false);

                if (claimResult == 0)
                {
                    // Another worker claimed it first.
                    removedFromSet++;
                    continue;
                }

                await _database.HashSetAsync(GetClaimMetaKey(operationId),
                [
                    new HashEntry("workerId", workerId),
                    new HashEntry("claimedAt", now.ToUnixTimeMilliseconds().ToString())
                ]).ConfigureAwait(false);

                // Persist claim metadata to the job store. If this fails, roll back the
                // Redis claim so the job returns to the pending queue.
                var claimed = job with
                {
                    Status = ExecutionJobStatus.Provisioning,
                    ClaimedBy = workerId,
                    ClaimedAt = now,
                    LastHeartbeatAt = now,
                    AttemptCount = job.AttemptCount + 1,
                    UpdatedAt = now,
                    CurrentPhase = "Claimed",
                    NextRetryAt = null
                };

                try
                {
                    await jobStore.SetAsync(claimed, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    await RollBackClaimAsync(operationId, job.Priority).ConfigureAwait(false);
                    throw;
                }

                Log.JobClaimed(logger, operationId, workerId);
                return operationId;
            }

            // Advance past entries that were not removed from the set.
            offset += candidates.Length - removedFromSet;
        }

        return null;
    }

    public async Task RequeueAsync(
        string operationId,
        OperationPriority priority = OperationPriority.Normal,
        TimeSpan? visibleAfter = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Atomically move from claimed → pending and update visibility metadata
        // in a single Lua evaluation to prevent the window where the job exists
        // in neither set.
        var metaKey = GetClaimMetaKey(operationId);

        if (visibleAfter.HasValue && visibleAfter.Value > TimeSpan.Zero)
        {
            var visibleAt = DateTimeOffset.UtcNow.Add(visibleAfter.Value);
            var score = ComputeScore(priority, visibleAt);

            await _database.ScriptEvaluateAsync(
                AtomicRequeueScript,
                [(RedisKey)ClaimedSetKey, (RedisKey)QueueKey, (RedisKey)metaKey],
                [(RedisValue)operationId, score, visibleAt.ToUnixTimeMilliseconds().ToString()])
                .ConfigureAwait(false);
        }
        else
        {
            var score = ComputeScore(priority, DateTimeOffset.UtcNow);

            await _database.ScriptEvaluateAsync(
                AtomicRequeueScript,
                [(RedisKey)ClaimedSetKey, (RedisKey)QueueKey, (RedisKey)metaKey],
                [(RedisValue)operationId, score, RedisValue.EmptyString])
                .ConfigureAwait(false);
        }

        Log.JobRequeued(logger, operationId);
    }

    public async Task RemoveAsync(
        string operationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await _database.SortedSetRemoveAsync(QueueKey, operationId).ConfigureAwait(false);
        await _database.SortedSetRemoveAsync(ClaimedSetKey, operationId).ConfigureAwait(false);
        await _database.KeyDeleteAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);

        Log.JobRemovedFromQueue(logger, operationId);
    }

    /// <inheritdoc />
    public async Task ReconcileStaleClaimsAsync(
        TimeSpan staleThreshold,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var thresholdScore = (double)DateTimeOffset.UtcNow.Subtract(staleThreshold).ToUnixTimeMilliseconds();
        var staleEntries = await _database.SortedSetRangeByScoreAsync(
            ClaimedSetKey, 0, thresholdScore).ConfigureAwait(false);

        foreach (var entry in staleEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!entry.HasValue)
            {
                continue;
            }

            var operationId = entry.ToString();
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);

            if (job == null || IsTerminal(job.Status))
            {
                // Record expired or already terminal. Clean up claimed set.
                await _database.SortedSetRemoveAsync(ClaimedSetKey, operationId).ConfigureAwait(false);
                await _database.KeyDeleteAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);
                continue;
            }

            if (job.Status == ExecutionJobStatus.Queued)
            {
                // Orphaned claim: removed from pending but store was never advanced.
                await _database.SortedSetRemoveAsync(ClaimedSetKey, operationId).ConfigureAwait(false);
                await _database.KeyDeleteAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);
                var score = ComputeScore(job.Priority, DateTimeOffset.UtcNow);
                await _database.SortedSetAddAsync(QueueKey, operationId, score).ConfigureAwait(false);
                Log.OrphanedClaimRequeued(logger, operationId);
            }
            else if (job.Status is ExecutionJobStatus.Provisioning or ExecutionJobStatus.Running
                     && job.LastHeartbeatAt.HasValue)
            {
                // Healthy long-running job: refresh the claimed-set score to the
                // last heartbeat time so future sweeps skip it until the heartbeat
                // genuinely goes cold. Expired heartbeats are handled by the main
                // reconciliation sweep in JobReconciliationService.
                await _database.SortedSetAddAsync(
                    ClaimedSetKey, operationId,
                    (double)job.LastHeartbeatAt.Value.ToUnixTimeMilliseconds()).ConfigureAwait(false);
            }
        }
    }

    public async Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.SortedSetLengthAsync(QueueKey).ConfigureAwait(false);
    }

    /// <summary>
    /// Best-effort rollback after a successful Lua claim when the subsequent
    /// job store update fails.
    /// </summary>
    private async Task RollBackClaimAsync(string operationId, OperationPriority priority)
    {
        try
        {
            await _database.SortedSetRemoveAsync(ClaimedSetKey, operationId).ConfigureAwait(false);
            await _database.KeyDeleteAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);
            var score = ComputeScore(priority, DateTimeOffset.UtcNow);
            await _database.SortedSetAddAsync(QueueKey, operationId, score).ConfigureAwait(false);
            Log.ClaimRolledBack(logger, operationId);
        }
        catch (Exception ex)
        {
            // Best-effort; stale-claim reconciliation is the safety net.
            Log.ClaimRollbackFailed(logger, operationId, ex);
        }
    }

    /// <summary>
    /// Computes a sort score that orders by priority (descending) then by enqueue time (ascending).
    /// Lower scores are dequeued first. Critical=0, High=1e12, Normal=2e12, Low=3e12, plus
    /// millisecond timestamp to break ties by FIFO order.
    /// </summary>
    private static double ComputeScore(OperationPriority priority, DateTimeOffset enqueueTime)
    {
        var priorityBucket = priority switch
        {
            OperationPriority.Critical => 0d,
            OperationPriority.High => 1_000_000_000_000d,
            OperationPriority.Normal => 2_000_000_000_000d,
            OperationPriority.Low => 3_000_000_000_000d,
            _ => 2_000_000_000_000d
        };

        return priorityBucket + enqueueTime.ToUnixTimeMilliseconds();
    }

    private static DateTimeOffset? GetVisibleAfter(HashEntry[] meta)
    {
        foreach (var entry in meta)
        {
            if (entry.Name == "visibleAfter" && long.TryParse(entry.Value.ToString(), out var ms))
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
            }
        }

        return null;
    }

    private static string GetClaimMetaKey(string operationId) => $"controlplane:jobqueue:meta:{operationId}";

    private static bool IsTerminal(ExecutionJobStatus status)
        => status is ExecutionJobStatus.Succeeded
            or ExecutionJobStatus.Failed
            or ExecutionJobStatus.Cancelled;

    private static partial class Log
    {
        [LoggerMessage(9020, LogLevel.Information, "Job enqueued: {OperationId}, Priority={Priority}")]
        public static partial void JobEnqueued(ILogger logger, string operationId, string priority);

        [LoggerMessage(9021, LogLevel.Information, "Job claimed: {OperationId} by worker {WorkerId}")]
        public static partial void JobClaimed(ILogger logger, string operationId, string workerId);

        [LoggerMessage(9022, LogLevel.Information, "Job requeued: {OperationId}")]
        public static partial void JobRequeued(ILogger logger, string operationId);

        [LoggerMessage(9023, LogLevel.Debug, "Job removed from queue: {OperationId}")]
        public static partial void JobRemovedFromQueue(ILogger logger, string operationId);

        [LoggerMessage(9024, LogLevel.Warning, "Orphaned claim requeued: {OperationId}")]
        public static partial void OrphanedClaimRequeued(ILogger logger, string operationId);

        [LoggerMessage(9025, LogLevel.Warning, "Claim rolled back after store update failure: {OperationId}")]
        public static partial void ClaimRolledBack(ILogger logger, string operationId);

        [LoggerMessage(9026, LogLevel.Error, "Claim rollback failed: {OperationId}")]
        public static partial void ClaimRollbackFailed(ILogger logger, string operationId, Exception exception);
    }
}
