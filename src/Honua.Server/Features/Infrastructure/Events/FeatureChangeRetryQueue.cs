// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Honua.Server.Features.Streaming;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Events;

internal interface IFeatureChangeRetryQueue
{
    Task<string> EnqueueAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<string> ReadQueuedIdsAsync(CancellationToken cancellationToken = default);
    Task ProcessQueuedAsync(string pendingId, CancellationToken cancellationToken = default);
}

internal sealed partial class FeatureChangeRetryQueue(
    IDistributedCache? pendingCache,
    Channel<PendingFeatureChangeSignal> queue,
    IFeatureChangeEventStore store,
    FeatureStreamSessionManager sessionManager,
    ILogger<FeatureChangeRetryQueue> logger,
    IConnectionMultiplexer? redis = null) : IFeatureChangeRetryQueue, IDisposable
{
    private const string PendingKeyPrefix = "featurechange:retry:";
    private const string PendingIndexKey = "featurechange:retry:index";
    private const string PendingIdsSetKey = "featurechange:retry:ids";
    private const string ClaimKeyPrefix = "featurechange:retry:claim:";
    private const string BroadcastStateKeyPrefix = "featurechange:retry:broadcast:";
    private const int PendingRetentionHours = 24;
    private static readonly TimeSpan ClaimTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RecoveryPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PendingRetention = TimeSpan.FromHours(PendingRetentionHours);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BroadcastClaimTtl = TimeSpan.FromMinutes(2);
    private const string BroadcastStateClaimed = "claimed";
    private const string BroadcastStateCompleted = "completed";
    private const string PersistPendingScript = """
        redis.call('SET', KEYS[1], ARGV[1], 'EX', tonumber(ARGV[2]))
        redis.call('SADD', KEYS[2], ARGV[3])
        return 1
        """;
    private readonly IDistributedCache? _pendingCache = pendingCache;
    private readonly Channel<PendingFeatureChangeSignal> _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly IFeatureChangeEventStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly FeatureStreamSessionManager _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    private readonly ILogger<FeatureChangeRetryQueue> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly IConnectionMultiplexer? _redis = redis;
    private readonly IDatabase? _redisDb = redis?.GetDatabase();
    private readonly ConcurrentDictionary<string, PendingFeatureChangePublish> _pendingPublishes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _scheduledRetries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _completedBroadcasts = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _cacheLock = new(1, 1);

    public async Task<string> EnqueueAsync(FeatureChangeEventRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var durableRequest = string.IsNullOrWhiteSpace(request.EventId)
            ? request with { EventId = Guid.NewGuid().ToString("N") }
            : request;

        var pending = new PendingFeatureChangePublish
        {
            PendingId = Guid.NewGuid().ToString("N"),
            Request = durableRequest,
            EnqueuedAt = DateTimeOffset.UtcNow
        };

        _pendingPublishes[pending.PendingId] = pending;
        await PersistPendingAsync(pending, cancellationToken).ConfigureAwait(false);
        await _queue.Writer.WriteAsync(new PendingFeatureChangeSignal(pending.PendingId), cancellationToken).ConfigureAwait(false);
        LogPublishQueued(_logger, pending.PendingId, durableRequest.ServiceId, durableRequest.LayerId, durableRequest.ObjectId);
        return pending.PendingId;
    }

    public async IAsyncEnumerable<string> ReadQueuedIdsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var recoveredPendingIds = await RecoverPendingIdsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var pendingId in recoveredPendingIds)
        {
            yield return pendingId;
        }

        var nextRecoveryAt = DateTimeOffset.UtcNow.Add(RecoveryPollInterval);
        while (!cancellationToken.IsCancellationRequested)
        {
            while (_queue.Reader.TryRead(out var signal))
            {
                yield return signal.PendingId;
            }

            var now = DateTimeOffset.UtcNow;
            if (now >= nextRecoveryAt)
            {
                var recoveredIds = await RecoverPendingIdsAsync(cancellationToken).ConfigureAwait(false);
                foreach (var pendingId in recoveredIds)
                {
                    yield return pendingId;
                }

                nextRecoveryAt = now.Add(RecoveryPollInterval);
                continue;
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var waitForQueue = _queue.Reader.WaitToReadAsync(waitCts.Token).AsTask();
            var waitForRecovery = Task.Delay(nextRecoveryAt - now, waitCts.Token);
            var completed = await Task.WhenAny(waitForQueue, waitForRecovery).ConfigureAwait(false);
            if (completed == waitForQueue)
            {
                if (!await waitForQueue.ConfigureAwait(false))
                {
                    waitCts.Cancel();
                    yield break;
                }

                waitCts.Cancel();
                continue;
            }

            waitCts.Cancel();
            try
            {
                await waitForQueue.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (waitCts.Token.IsCancellationRequested)
            {
                // Ignore expected cancellation for the alternate waiter.
            }
        }
    }

    public async Task ProcessQueuedAsync(string pendingId, CancellationToken cancellationToken = default)
    {
        var leaseCoordinator = await TryAcquireClaimLeaseAsync(pendingId).ConfigureAwait(false);
        if (_redisDb != null && leaseCoordinator == null)
        {
            return;
        }

        using var renewalCts = leaseCoordinator is null ? null : new CancellationTokenSource();
        using var processingCts = leaseCoordinator is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseCoordinator.LeaseLostToken);
        var renewalTask = leaseCoordinator is null
            ? Task.CompletedTask
            : RenewClaimLeaseAsync(leaseCoordinator, renewalCts!.Token);
        var processingToken = processingCts?.Token ?? cancellationToken;

        var pending = await TryGetPendingAsync(pendingId, processingToken).ConfigureAwait(false);
        if (pending == null)
        {
            if (renewalCts != null)
            {
                renewalCts.Cancel();
                await renewalTask.ConfigureAwait(false);
                await leaseCoordinator!.ReleaseAsync().ConfigureAwait(false);
            }

            return;
        }

        if (pending.BroadcastCompleted)
        {
            if (renewalCts != null)
            {
                renewalCts.Cancel();
                await renewalTask.ConfigureAwait(false);
                await leaseCoordinator!.ReleaseAsync().ConfigureAwait(false);
            }

            _pendingPublishes.TryRemove(pendingId, out _);
            await RemovePendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
            LogRetryAlreadyDelivered(_logger, pendingId);
            return;
        }

        try
        {
            var persisted = await _store.AppendAsync(pending.Request, processingToken).ConfigureAwait(false);
            processingToken.ThrowIfCancellationRequested();

            var broadcastState = await TryAcquireBroadcastClaimAsync(persisted.EventId).ConfigureAwait(false);
            if (broadcastState == BroadcastClaimResult.InProgress)
            {
                ScheduleRetry(pendingId);
                return;
            }

            if (broadcastState == BroadcastClaimResult.Completed)
            {
                pending = pending with { BroadcastCompleted = true };
                _pendingPublishes[pendingId] = pending;
                await PersistPendingAsync(pending, CancellationToken.None).ConfigureAwait(false);

                _pendingPublishes.TryRemove(pendingId, out _);
                await RemovePendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
                LogRetryAlreadyDelivered(_logger, pendingId);
                return;
            }

            processingToken.ThrowIfCancellationRequested();
            Broadcast(persisted);
            await MarkBroadcastCompletedAsync(persisted.EventId).ConfigureAwait(false);

            var completed = pending with { BroadcastCompleted = true };
            pending = completed;
            _pendingPublishes[pendingId] = completed;
            await PersistPendingAsync(completed, CancellationToken.None).ConfigureAwait(false);

            _pendingPublishes.TryRemove(pendingId, out _);
            await RemovePendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
            LogRetrySucceeded(_logger, pendingId, pending.AttemptCount + 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ScheduleRetry(pendingId);
            throw;
        }
        catch (OperationCanceledException)
        {
            ScheduleRetry(pendingId);
        }
        catch (Exception ex)
        {
            var updated = pending with
            {
                AttemptCount = pending.AttemptCount + 1,
                LastAttemptAt = DateTimeOffset.UtcNow
            };

            _pendingPublishes[pendingId] = updated;
            await PersistPendingAsync(updated, CancellationToken.None).ConfigureAwait(false);
            ScheduleRetry(pendingId);
            LogRetryFailed(_logger, pendingId, updated.AttemptCount, ex);
        }
        finally
        {
            if (renewalCts != null)
            {
                renewalCts.Cancel();
                await renewalTask.ConfigureAwait(false);
                await leaseCoordinator!.ReleaseAsync().ConfigureAwait(false);
                leaseCoordinator.Dispose();
            }
        }
    }

    private void Broadcast(FeatureChangeEvent featureEvent)
    {
        // Broadcast() returns the local delivery count only; cross-node fan-out is handled separately.
        var delivered = _sessionManager.Broadcast(
            FeatureStreamMessage.Data(
                FeatureStreamPublisher.ToEnvelope(featureEvent),
                featureEvent.GeometryEnvelope,
                featureEvent.PropertiesJson));

        LogLocalEventBroadcast(_logger, delivered, featureEvent.Cursor);
    }

    private async Task<BroadcastClaimResult> TryAcquireBroadcastClaimAsync(string eventId)
    {
        if (_redisDb != null)
        {
            var stateKey = GetBroadcastStateKey(eventId);
            var claimed = await _redisDb.StringSetAsync(
                    stateKey,
                    BroadcastStateClaimed,
                    BroadcastClaimTtl,
                    when: When.NotExists)
                .ConfigureAwait(false);
            if (claimed)
            {
                return BroadcastClaimResult.Acquired;
            }

            var existingState = await _redisDb.StringGetAsync(stateKey).ConfigureAwait(false);
            return string.Equals(existingState.ToString(), BroadcastStateCompleted, StringComparison.Ordinal)
                ? BroadcastClaimResult.Completed
                : BroadcastClaimResult.InProgress;
        }

        return _completedBroadcasts.ContainsKey(eventId)
            ? BroadcastClaimResult.Completed
            : BroadcastClaimResult.Acquired;
    }

    private async Task MarkBroadcastCompletedAsync(string eventId)
    {
        if (_redisDb != null)
        {
            await _redisDb.StringSetAsync(
                    GetBroadcastStateKey(eventId),
                    BroadcastStateCompleted,
                    PendingRetention)
                .ConfigureAwait(false);
            return;
        }

        _completedBroadcasts[eventId] = 0;
    }

    private async Task<PendingFeatureChangePublish?> TryGetPendingAsync(string pendingId, CancellationToken cancellationToken)
    {
        if (_pendingPublishes.TryGetValue(pendingId, out var livePending))
        {
            return livePending;
        }

        if (_redisDb == null &&
            _pendingCache == null)
        {
            return null;
        }

        if (_redisDb != null)
        {
            var redisJson = await _redisDb.StringGetAsync(GetPendingKey(pendingId)).ConfigureAwait(false);
            if (redisJson.HasValue)
            {
                var restoredFromRedis = JsonSerializer.Deserialize(
                    redisJson.ToString()!,
                    FeatureChangeEventsJsonContext.Default.PendingFeatureChangePublish);
                if (restoredFromRedis != null)
                {
                    _pendingPublishes[pendingId] = restoredFromRedis;
                    return restoredFromRedis;
                }

                await RemovePendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
                return null;
            }

            _pendingPublishes.TryRemove(pendingId, out _);
        }

        if (_pendingCache == null)
        {
            return null;
        }

        var json = await _pendingCache.GetStringAsync(GetPendingKey(pendingId), cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            _pendingPublishes.TryRemove(pendingId, out _);
            return null;
        }

        var restored = JsonSerializer.Deserialize(
            json,
            FeatureChangeEventsJsonContext.Default.PendingFeatureChangePublish);
        if (restored == null)
        {
            await RemovePendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        _pendingPublishes[pendingId] = restored;
        return restored;
    }

    private async Task<IReadOnlyList<string>> RecoverPendingIdsAsync(CancellationToken cancellationToken)
    {
        if (_redisDb != null)
        {
            var members = await _redisDb.SetMembersAsync(PendingIdsSetKey).ConfigureAwait(false);
            if (members.Length == 0)
            {
                return [];
            }

            var recovered = new List<string>(members.Length);
            foreach (var member in members)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pendingId = member.ToString();
                if (string.IsNullOrWhiteSpace(pendingId))
                {
                    await _redisDb.SetRemoveAsync(PendingIdsSetKey, member).ConfigureAwait(false);
                    continue;
                }

                var pending = await TryGetPendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
                if (pending == null)
                {
                    await _redisDb.SetRemoveAsync(PendingIdsSetKey, member).ConfigureAwait(false);
                    continue;
                }

                recovered.Add(pendingId);
            }

            return recovered;
        }

        if (_pendingCache == null)
        {
            return [];
        }

        var indexJson = await _pendingCache.GetStringAsync(PendingIndexKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(indexJson))
        {
            return [];
        }

        var index = JsonSerializer.Deserialize(indexJson, FeatureChangeEventsJsonContext.Default.PendingFeatureChangeIndex);
        if (index?.PendingIds == null || index.PendingIds.Length == 0)
        {
            return [];
        }

        var recoveredIds = new List<string>(index.PendingIds.Length);
        foreach (var pendingId in index.PendingIds.Distinct(StringComparer.Ordinal))
        {
            var pending = await TryGetPendingAsync(pendingId, cancellationToken).ConfigureAwait(false);
            if (pending == null)
            {
                continue;
            }

            recoveredIds.Add(pendingId);
        }

        return recoveredIds;
    }

    private async Task PersistPendingAsync(PendingFeatureChangePublish pending, CancellationToken cancellationToken)
    {
        var pendingJson = JsonSerializer.Serialize(
            pending,
            FeatureChangeEventsJsonContext.Default.PendingFeatureChangePublish);

        if (_redisDb != null)
        {
            await _redisDb.ScriptEvaluateAsync(
                PersistPendingScript,
                [GetPendingKey(pending.PendingId), PendingIdsSetKey],
                [pendingJson, (long)PendingRetention.TotalSeconds, pending.PendingId]).ConfigureAwait(false);
            return;
        }

        if (_pendingCache == null)
        {
            return;
        }

        await _pendingCache.SetStringAsync(
            GetPendingKey(pending.PendingId),
            pendingJson,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = PendingRetention },
            cancellationToken).ConfigureAwait(false);

        await UpdateIndexAsync(
            ids => ids.Add(pending.PendingId),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RemovePendingAsync(string pendingId, CancellationToken cancellationToken)
    {
        if (_redisDb != null)
        {
            await _redisDb.KeyDeleteAsync(GetPendingKey(pendingId)).ConfigureAwait(false);
            await _redisDb.SetRemoveAsync(PendingIdsSetKey, pendingId).ConfigureAwait(false);
            await _redisDb.KeyDeleteAsync(GetClaimKey(pendingId)).ConfigureAwait(false);
            return;
        }

        if (_pendingCache != null)
        {
            await _pendingCache.RemoveAsync(GetPendingKey(pendingId), cancellationToken).ConfigureAwait(false);
            await UpdateIndexAsync(
                ids => ids.RemoveAll(id => string.Equals(id, pendingId, StringComparison.Ordinal)),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateIndexAsync(Action<List<string>> update, CancellationToken cancellationToken)
    {
        if (_pendingCache == null)
        {
            return;
        }

        await _cacheLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var existingJson = await _pendingCache.GetStringAsync(PendingIndexKey, cancellationToken).ConfigureAwait(false);
            var ids = string.IsNullOrWhiteSpace(existingJson)
                ? []
                : JsonSerializer.Deserialize(existingJson, FeatureChangeEventsJsonContext.Default.PendingFeatureChangeIndex)?.PendingIds?.ToList()
                    ?? [];

            update(ids);
            ids = ids
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (ids.Count == 0)
            {
                await _pendingCache.RemoveAsync(PendingIndexKey, cancellationToken).ConfigureAwait(false);
                return;
            }

            var indexJson = JsonSerializer.Serialize(
                new PendingFeatureChangeIndex { PendingIds = [.. ids] },
                FeatureChangeEventsJsonContext.Default.PendingFeatureChangeIndex);

            await _pendingCache.SetStringAsync(
                PendingIndexKey,
                indexJson,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = PendingRetention },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    private void ScheduleRetry(string pendingId)
    {
        if (!_scheduledRetries.TryAdd(pendingId, 0))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(RetryDelay).ConfigureAwait(false);
                await _queue.Writer.WriteAsync(new PendingFeatureChangeSignal(pendingId)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogRetrySchedulingFailed(_logger, pendingId, ex);
            }
            finally
            {
                _scheduledRetries.TryRemove(pendingId, out _);
            }
        });
    }

    public void Dispose() => _cacheLock.Dispose();

    private static string GetPendingKey(string pendingId) => $"{PendingKeyPrefix}{pendingId}";
    private static string GetClaimKey(string pendingId) => $"{ClaimKeyPrefix}{pendingId}";
    private static string GetBroadcastStateKey(string eventId) => $"{BroadcastStateKeyPrefix}{eventId}";

    private async Task<RedisLeaseCoordinator?> TryAcquireClaimLeaseAsync(string pendingId)
    {
        if (_redis == null)
        {
            return null;
        }

        var leaseCoordinator = new RedisLeaseCoordinator(_redis, GetClaimKey(pendingId), ClaimTtl);
        return await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false)
            ? leaseCoordinator
            : null;
    }

    private async Task RenewClaimLeaseAsync(RedisLeaseCoordinator leaseCoordinator, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(ClaimTtl.TotalMilliseconds / 3d), cancellationToken).ConfigureAwait(false);
                if (!await leaseCoordinator.TryAcquireOrExtendAsync().ConfigureAwait(false))
                {
                    LogRetryClaimLost(_logger);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    [LoggerMessage(EventId = 9110, Level = LogLevel.Warning, Message = "Queued feature-change publish retry {PendingId} for service {ServiceId}, layer {LayerId}, object {ObjectId}.")]
    private static partial void LogPublishQueued(ILogger logger, string pendingId, string serviceId, int layerId, long objectId);

    [LoggerMessage(EventId = 9111, Level = LogLevel.Warning, Message = "Feature-change publish retry {PendingId} failed on attempt {AttemptCount}.")]
    private static partial void LogRetryFailed(ILogger logger, string pendingId, int attemptCount, Exception exception);

    [LoggerMessage(EventId = 9112, Level = LogLevel.Information, Message = "Feature-change publish retry {PendingId} succeeded after {AttemptCount} attempts.")]
    private static partial void LogRetrySucceeded(ILogger logger, string pendingId, int attemptCount);

    [LoggerMessage(EventId = 9114, Level = LogLevel.Information, Message = "Feature-change publish retry {PendingId} was already delivered; skipping broadcast.")]
    private static partial void LogRetryAlreadyDelivered(ILogger logger, string pendingId);

    [LoggerMessage(EventId = 9115, Level = LogLevel.Debug, Message = "Feature-change retry event delivered locally to {Count} sessions (cursor={Cursor})")]
    private static partial void LogLocalEventBroadcast(ILogger logger, int count, long cursor);

    [LoggerMessage(EventId = 9113, Level = LogLevel.Warning, Message = "Failed to schedule retry for feature-change publish {PendingId}.")]
    private static partial void LogRetrySchedulingFailed(ILogger logger, string pendingId, Exception exception);

    [LoggerMessage(EventId = 9116, Level = LogLevel.Warning, Message = "Failed to persist feature-change retry state for {PendingId}.")]
    private static partial void LogRetryPersistenceFailed(ILogger logger, string pendingId, Exception exception);

    [LoggerMessage(EventId = 9115, Level = LogLevel.Warning, Message = "Feature-change retry claim renewal was lost while processing a pending publish.")]
    private static partial void LogRetryClaimLost(ILogger logger);
}

internal sealed record PendingFeatureChangePublish
{
    public required string PendingId { get; init; }
    public required FeatureChangeEventRequest Request { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public int AttemptCount { get; init; }
    public DateTimeOffset? LastAttemptAt { get; init; }
    public bool BroadcastCompleted { get; init; }
}

internal sealed record PendingFeatureChangeIndex
{
    public string[] PendingIds { get; init; } = [];
}

internal enum BroadcastClaimResult
{
    Acquired,
    InProgress,
    Completed
}

internal readonly record struct PendingFeatureChangeSignal(string PendingId);
