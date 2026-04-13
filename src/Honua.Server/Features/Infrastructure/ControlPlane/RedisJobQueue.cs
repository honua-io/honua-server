// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

/// <summary>
/// Redis-backed durable job queue with atomic claim semantics.
/// Uses a sorted set keyed by priority score for dequeue ordering and
/// per-job metadata hashes for claim tracking.
/// </summary>
internal sealed partial class RedisJobQueue(
    IConnectionMultiplexer redis,
    IExecutionJobStore jobStore,
    ILogger<RedisJobQueue> logger) : IJobQueue
{
    private const string QueueKey = "controlplane:jobqueue:pending";
    private const string ClaimedSetKey = "controlplane:jobqueue:claimed";

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

        // Scan the sorted set from highest priority (lowest score) to find a claimable job.
        // We use ZPOPMIN-style iteration rather than a Lua script to stay simple and
        // avoid blocking other queue consumers during the job store lookup.
        const int batchSize = 10;
        var now = DateTimeOffset.UtcNow;

        var candidates = await _database.SortedSetRangeByRankAsync(
            QueueKey, 0, batchSize - 1).ConfigureAwait(false);

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
                continue;
            }

            // Load the job record to check kind filter and current status.
            var job = await jobStore.GetAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (job == null || IsTerminal(job.Status))
            {
                // Stale entry; remove from queue.
                await _database.SortedSetRemoveAsync(QueueKey, operationId).ConfigureAwait(false);
                continue;
            }

            if (acceptedKinds != null && !acceptedKinds.Contains(job.Spec.Kind))
            {
                continue;
            }

            // Attempt atomic claim: remove from pending and add to claimed.
            var removed = await _database.SortedSetRemoveAsync(QueueKey, operationId).ConfigureAwait(false);
            if (!removed)
            {
                // Another worker claimed it first.
                continue;
            }

            await _database.SortedSetAddAsync(ClaimedSetKey, operationId, now.ToUnixTimeMilliseconds())
                .ConfigureAwait(false);

            await _database.HashSetAsync(GetClaimMetaKey(operationId),
            [
                new HashEntry("workerId", workerId),
                new HashEntry("claimedAt", now.ToUnixTimeMilliseconds().ToString())
            ]).ConfigureAwait(false);

            // Update the job record with claim metadata.
            var claimed = job with
            {
                Status = ExecutionJobStatus.Provisioning,
                ClaimedBy = workerId,
                ClaimedAt = now,
                LastHeartbeatAt = now,
                AttemptCount = job.AttemptCount + 1,
                UpdatedAt = now,
                CurrentPhase = "Claimed"
            };
            await jobStore.SetAsync(claimed, cancellationToken: cancellationToken).ConfigureAwait(false);

            Log.JobClaimed(logger, operationId, workerId);
            return operationId;
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

        // Remove from claimed set.
        await _database.SortedSetRemoveAsync(ClaimedSetKey, operationId).ConfigureAwait(false);

        // Set visibility delay if requested.
        if (visibleAfter.HasValue && visibleAfter.Value > TimeSpan.Zero)
        {
            var visibleAt = DateTimeOffset.UtcNow.Add(visibleAfter.Value);
            await _database.HashSetAsync(GetClaimMetaKey(operationId),
            [
                new HashEntry("visibleAfter", visibleAt.ToUnixTimeMilliseconds().ToString())
            ]).ConfigureAwait(false);
        }
        else
        {
            await _database.KeyDeleteAsync(GetClaimMetaKey(operationId)).ConfigureAwait(false);
        }

        var score = ComputeScore(priority, DateTimeOffset.UtcNow);
        await _database.SortedSetAddAsync(QueueKey, operationId, score).ConfigureAwait(false);

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

    public async Task<long> GetQueueDepthAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _database.SortedSetLengthAsync(QueueKey).ConfigureAwait(false);
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
    }
}
