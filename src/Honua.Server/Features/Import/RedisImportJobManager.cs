// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Import;

/// <summary>
/// Redis-based distributed import job manager with in-memory fallback.
/// Uses StackExchange.Redis primitives when available, falling back to IDistributedCache.
/// </summary>
internal sealed partial class RedisImportJobManager : IDistributedImportJobManager, IDisposable
{
    private readonly RedisJobQueue _jobQueue;
    private readonly RedisLeaderElection _leaderElection;
    private readonly RedisProgressStore<EsriImportProgress> _progressStore;
    private readonly RedisProgressStore<EsriImportRequest> _requestStore;

    public RedisImportJobManager(
        IDistributedCache? distributedCache,
        ILogger<RedisImportJobManager> logger,
        IConnectionMultiplexer? redis = null)
    {
        var instanceId = $"{Environment.MachineName}-{Environment.ProcessId}";

        _jobQueue = new RedisJobQueue(distributedCache, redis, logger, "esri:import:queue");
        _leaderElection = new RedisLeaderElection(distributedCache, redis, logger, "esri:import:leader", instanceId);
        _progressStore = new RedisProgressStore<EsriImportProgress>(
            distributedCache, logger, "esri:import:progress:", EsriImportJsonContext.Default.EsriImportProgress);
        _requestStore = new RedisProgressStore<EsriImportRequest>(
            distributedCache, logger, "esri:import:request:", EsriImportJsonContext.Default.EsriImportRequest);
    }

    public IDistributedJobQueueService JobQueue => _jobQueue;
    public IDistributedLeaderElection LeaderElection => _leaderElection;
    public IDistributedProgressStore<EsriImportProgress> ProgressStore => _progressStore;
    public IDistributedProgressStore<EsriImportRequest> RequestStore => _requestStore;

    public void Dispose()
    {
        _leaderElection.Dispose();
    }
}

/// <summary>
/// Redis-based job queue using StackExchange.Redis lists with IDistributedCache fallback.
/// Note: IDistributedCache doesn't support LPUSH/BRPOP, so we use a polling approach.
/// </summary>
internal sealed partial class RedisJobQueue : IDistributedJobQueueService
{
    private readonly IDistributedCache? _cache;
    private readonly IDatabase? _redisDb;
    private readonly ILogger _logger;
    private readonly string _queueKey;
    private readonly ConcurrentQueue<string> _fallbackQueue = new();
    private volatile bool _useRedis;
    private volatile bool _isUsingFallback;

    public RedisJobQueue(IDistributedCache? cache, IConnectionMultiplexer? redis, ILogger logger, string queueKey)
    {
        _cache = cache;
        _redisDb = redis?.GetDatabase();
        _logger = logger;
        _queueKey = queueKey;
        _useRedis = _redisDb != null;
        _isUsingFallback = !_useRedis && cache == null;

        if (_isUsingFallback)
        {
            Log.UsingFallbackQueue(_logger);
        }
    }

    public async Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (_useRedis && _redisDb != null)
        {
            try
            {
                await _redisDb.ListRightPushAsync(_queueKey, jobId).ConfigureAwait(false);
                Log.JobEnqueued(_logger, jobId, "redis");
                return;
            }
            catch (Exception ex)
            {
                Log.RedisFailed(_logger, "enqueue", ex);
                _useRedis = false;
                _isUsingFallback = _cache == null;
            }
        }

        if (_isUsingFallback || _cache == null)
        {
            _fallbackQueue.Enqueue(jobId);
            Log.JobEnqueued(_logger, jobId, "fallback");
            return;
        }

        try
        {
            // Use a list-like pattern with distributed cache
            // Store jobs as individual keys with a counter for ordering
            var counterKey = $"{_queueKey}:counter";
            var counterBytes = await _cache.GetAsync(counterKey, cancellationToken);
            var counter = counterBytes != null ? BitConverter.ToInt64(counterBytes) + 1 : 1;

            await _cache.SetAsync(counterKey, BitConverter.GetBytes(counter),
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(24) },
                cancellationToken);

            var itemKey = $"{_queueKey}:{counter:D10}";
            await _cache.SetStringAsync(itemKey, jobId,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) },
                cancellationToken);

            // Track pending items
            var pendingKey = $"{_queueKey}:pending";
            var pending = await _cache.GetStringAsync(pendingKey, cancellationToken) ?? "";
            pending = string.IsNullOrEmpty(pending) ? itemKey : $"{pending},{itemKey}";
            await _cache.SetStringAsync(pendingKey, pending,
                new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(24) },
                cancellationToken);

            Log.JobEnqueued(_logger, jobId, "redis");
        }
        catch (Exception ex)
        {
            Log.RedisFailed(_logger, "enqueue", ex);
            _isUsingFallback = true;
            _fallbackQueue.Enqueue(jobId);
        }
    }

    public async Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (_useRedis && _redisDb != null)
            {
                try
                {
                    var jobValue = await _redisDb.ListLeftPopAsync(_queueKey).ConfigureAwait(false);
                    if (jobValue.HasValue)
                    {
                        var jobId = jobValue.ToString();
                        Log.JobDequeued(_logger, jobId, "redis");
                        return jobId;
                    }
                }
                catch (Exception ex)
                {
                    Log.RedisFailed(_logger, "dequeue", ex);
                    _useRedis = false;
                    _isUsingFallback = _cache == null;
                }
            }

            if (!_useRedis)
            {
                if (_isUsingFallback || _cache == null)
                {
                    if (_fallbackQueue.TryDequeue(out var fallbackJob))
                    {
                        Log.JobDequeued(_logger, fallbackJob, "fallback");
                        return fallbackJob;
                    }
                }
                else
                {
                    try
                    {
                        var pendingKey = $"{_queueKey}:pending";
                        var pending = await _cache.GetStringAsync(pendingKey, cancellationToken);

                        if (!string.IsNullOrEmpty(pending))
                        {
                            var items = pending.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (items.Length > 0)
                            {
                                var itemKey = items[0];
                                var jobId = await _cache.GetStringAsync(itemKey, cancellationToken);

                                if (jobId != null)
                                {
                                    // Remove from pending list
                                    var newPending = string.Join(",", items.Skip(1));
                                    await _cache.SetStringAsync(pendingKey, newPending,
                                        new DistributedCacheEntryOptions { SlidingExpiration = TimeSpan.FromHours(24) },
                                        cancellationToken);

                                    // Remove the item
                                    await _cache.RemoveAsync(itemKey, cancellationToken);

                                    Log.JobDequeued(_logger, jobId, "redis");
                                    return jobId;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.RedisFailed(_logger, "dequeue", ex);
                        _isUsingFallback = true;
                    }
                }
            }

            // Wait before polling again
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }

        return null;
    }

    public async Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default)
    {
        if (_useRedis && _redisDb != null)
        {
            try
            {
                return (long)await _redisDb.ListLengthAsync(_queueKey).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.RedisFailed(_logger, "length", ex);
                _useRedis = false;
                _isUsingFallback = _cache == null;
            }
        }

        if (_isUsingFallback || _cache == null)
        {
            return _fallbackQueue.Count;
        }

        try
        {
            var pendingKey = $"{_queueKey}:pending";
            var pending = await _cache.GetStringAsync(pendingKey, cancellationToken);
            return string.IsNullOrEmpty(pending) ? 0 : pending.Split(',').Length;
        }
        catch
        {
            return _fallbackQueue.Count;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7600, LogLevel.Information, "Using in-memory fallback queue (Redis not available)")]
        public static partial void UsingFallbackQueue(ILogger logger);

        [LoggerMessage(7601, LogLevel.Debug, "Job {JobId} enqueued to {QueueType}")]
        public static partial void JobEnqueued(ILogger logger, string jobId, string queueType);

        [LoggerMessage(7602, LogLevel.Debug, "Job {JobId} dequeued from {QueueType}")]
        public static partial void JobDequeued(ILogger logger, string jobId, string queueType);

        [LoggerMessage(7603, LogLevel.Warning, "Redis {Operation} failed, using fallback")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);
    }
}

/// <summary>
/// Redis-based leader election using StackExchange.Redis locks with IDistributedCache fallback.
/// </summary>
internal sealed partial class RedisLeaderElection : IDistributedLeaderElection, IDisposable
{
    private readonly IDistributedCache? _cache;
    private readonly IDatabase? _redisDb;
    private readonly ILogger _logger;
    private readonly string _lockKey;
    private readonly string _instanceId;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);
    private readonly Timer? _heartbeatTimer;
    private volatile bool _useRedis;
    private volatile bool _isLeader;
    private volatile bool _disposed;

    public RedisLeaderElection(
        IDistributedCache? cache,
        IConnectionMultiplexer? redis,
        ILogger logger,
        string lockKey,
        string instanceId)
    {
        _cache = cache;
        _redisDb = redis?.GetDatabase();
        _useRedis = _redisDb != null;
        _logger = logger;
        _lockKey = lockKey;
        _instanceId = instanceId;

        if (!_useRedis && _cache == null)
        {
            // In fallback mode, this instance is always the leader
            _isLeader = true;
            Log.FallbackLeader(_logger, _instanceId);
        }
        else
        {
            // Start heartbeat timer
            _heartbeatTimer = new Timer(HeartbeatCallback, null, Timeout.Infinite, Timeout.Infinite);
        }
    }

    public bool IsLeader => _isLeader;
    public string InstanceId => _instanceId;

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_useRedis && _redisDb != null)
        {
            try
            {
                var acquired = await _redisDb.LockTakeAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
                if (acquired)
                {
                    _isLeader = true;
                    _heartbeatTimer?.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                    Log.LeadershipAcquired(_logger, _instanceId);
                    return true;
                }

                _isLeader = false;
                return false;
            }
            catch (Exception ex)
            {
                Log.LeadershipError(_logger, "acquire", ex);
                _useRedis = false;
            }
        }

        if (_cache == null)
        {
            _isLeader = true;
            return true;
        }

        try
        {
            // Check if lock exists
            var currentLeader = await _cache.GetStringAsync(_lockKey, cancellationToken);

            if (currentLeader == _instanceId)
            {
                // We already hold the lock, just refresh
                await RefreshLockAsync(cancellationToken);
                return true;
            }

            if (currentLeader != null)
            {
                // Someone else holds the lock
                _isLeader = false;
                return false;
            }

            // Try to acquire the lock
            // Note: IDistributedCache doesn't have atomic SetNX, so there's a small race window
            // For production, consider using StackExchange.Redis directly with SETNX
            await _cache.SetStringAsync(_lockKey, _instanceId,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _leaseDuration },
                cancellationToken);

            // Verify we got it
            currentLeader = await _cache.GetStringAsync(_lockKey, cancellationToken);
            if (currentLeader == _instanceId)
            {
                _isLeader = true;
                _heartbeatTimer?.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                Log.LeadershipAcquired(_logger, _instanceId);
                return true;
            }

            _isLeader = false;
            return false;
        }
        catch (Exception ex)
        {
            Log.LeadershipError(_logger, "acquire", ex);
            // In case of Redis failure, assume leadership to continue processing
            _isLeader = true;
            return true;
        }
    }

    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (_useRedis && _redisDb != null)
        {
            if (!_isLeader)
            {
                return _isLeader;
            }

            try
            {
                var extended = await _redisDb.LockExtendAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
                if (!extended)
                {
                    _isLeader = false;
                    _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                    Log.LeadershipLost(_logger, _instanceId);
                }

                return _isLeader;
            }
            catch (Exception ex)
            {
                Log.LeadershipError(_logger, "heartbeat", ex);
                return _isLeader; // Keep current state on error
            }
        }

        if (_cache == null || !_isLeader)
        {
            return _isLeader;
        }

        try
        {
            var currentLeader = await _cache.GetStringAsync(_lockKey, cancellationToken);
            if (currentLeader != _instanceId)
            {
                _isLeader = false;
                _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                Log.LeadershipLost(_logger, _instanceId);
                return false;
            }

            await RefreshLockAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            Log.LeadershipError(_logger, "heartbeat", ex);
            return _isLeader; // Keep current state on error
        }
    }

    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        if (_useRedis && _redisDb != null)
        {
            try
            {
                _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                await _redisDb.LockReleaseAsync(_lockKey, _instanceId).ConfigureAwait(false);
                Log.LeadershipReleased(_logger, _instanceId);
                _isLeader = false;
            }
            catch (Exception ex)
            {
                Log.LeadershipError(_logger, "release", ex);
            }

            return;
        }

        if (_cache == null)
        {
            return;
        }

        try
        {
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);

            var currentLeader = await _cache.GetStringAsync(_lockKey, cancellationToken);
            if (currentLeader == _instanceId)
            {
                await _cache.RemoveAsync(_lockKey, cancellationToken);
                Log.LeadershipReleased(_logger, _instanceId);
            }

            _isLeader = false;
        }
        catch (Exception ex)
        {
            Log.LeadershipError(_logger, "release", ex);
        }
    }

    private async Task RefreshLockAsync(CancellationToken cancellationToken)
    {
        await _cache!.SetStringAsync(_lockKey, _instanceId,
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _leaseDuration },
            cancellationToken);
    }

    private void HeartbeatCallback(object? state)
    {
        if (_disposed)
            return;

        _ = HeartbeatAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _heartbeatTimer?.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(7610, LogLevel.Information, "Instance {InstanceId} is leader (fallback mode)")]
        public static partial void FallbackLeader(ILogger logger, string instanceId);

        [LoggerMessage(7611, LogLevel.Information, "Instance {InstanceId} acquired leadership")]
        public static partial void LeadershipAcquired(ILogger logger, string instanceId);

        [LoggerMessage(7612, LogLevel.Warning, "Instance {InstanceId} lost leadership")]
        public static partial void LeadershipLost(ILogger logger, string instanceId);

        [LoggerMessage(7613, LogLevel.Information, "Instance {InstanceId} released leadership")]
        public static partial void LeadershipReleased(ILogger logger, string instanceId);

        [LoggerMessage(7614, LogLevel.Warning, "Leadership {Operation} error")]
        public static partial void LeadershipError(ILogger logger, string operation, Exception exception);
    }
}

/// <summary>
/// Redis-based progress store using IDistributedCache.
/// </summary>
internal sealed partial class RedisProgressStore<T> : IDistributedProgressStore<T> where T : class
{
    private readonly IDistributedCache? _cache;
    private readonly ILogger _logger;
    private readonly string _keyPrefix;
    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private readonly ConcurrentDictionary<string, T> _fallbackStore = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackExpiry = new();
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan _fallbackCleanupInterval = TimeSpan.FromMinutes(5);
    private long _lastCleanupTick = Environment.TickCount64;
    private volatile bool _isUsingFallback;

    public RedisProgressStore(
        IDistributedCache? cache,
        ILogger logger,
        string keyPrefix,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        _cache = cache;
        _logger = logger;
        _keyPrefix = keyPrefix;
        _jsonTypeInfo = jsonTypeInfo;
        _isUsingFallback = cache == null;
    }

    public async Task SetProgressAsync(string jobId, T progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

        if (_isUsingFallback || _cache == null)
        {
            _fallbackStore[key] = progress;
            _fallbackExpiry[key] = DateTime.UtcNow.Add(effectiveTtl);
            CleanupFallbackIfNeeded(enforceMax: true);
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(progress, _jsonTypeInfo);
            await _cache.SetStringAsync(key, json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl },
                cancellationToken);
        }
        catch (Exception ex)
        {
            Log.RedisFailed(_logger, "set progress", ex);
            _isUsingFallback = true;
            _fallbackStore[key] = progress;
            _fallbackExpiry[key] = DateTime.UtcNow.Add(effectiveTtl);
            CleanupFallbackIfNeeded(enforceMax: true);
        }
    }

    public async Task<T?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";

        if (_isUsingFallback || _cache == null)
        {
            CleanupFallbackIfNeeded(enforceMax: false);

            if (_fallbackStore.TryGetValue(key, out var fallbackProgress))
            {
                if (_fallbackExpiry.TryGetValue(key, out var expiry) && expiry > DateTime.UtcNow)
                {
                    return fallbackProgress;
                }

                _fallbackStore.TryRemove(key, out _);
                _fallbackExpiry.TryRemove(key, out _);
            }
            return null;
        }

        try
        {
            var json = await _cache.GetStringAsync(key, cancellationToken);
            if (json == null)
                return null;

            return JsonSerializer.Deserialize(json, _jsonTypeInfo);
        }
        catch (Exception ex)
        {
            Log.RedisFailed(_logger, "get progress", ex);
            _isUsingFallback = true;
            return _fallbackStore.TryGetValue(key, out var progress) ? progress : null;
        }
    }

    public async Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";

        _fallbackStore.TryRemove(key, out _);
        _fallbackExpiry.TryRemove(key, out _);

        if (_cache != null && !_isUsingFallback)
        {
            try
            {
                await _cache.RemoveAsync(key, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.RedisFailed(_logger, "delete progress", ex);
            }
        }
    }

    public Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
    {
        // For fallback, return in-memory keys
        CleanupFallbackIfNeeded(enforceMax: false);
        var now = DateTime.UtcNow;
        var activeIds = _fallbackStore.Keys
            .Where(k => k.StartsWith(_keyPrefix, StringComparison.Ordinal))
            .Where(k => !_fallbackExpiry.TryGetValue(k, out var expiry) || expiry > now)
            .Select(k => k[_keyPrefix.Length..])
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(activeIds);

        // Note: For Redis, we'd need SCAN which IDistributedCache doesn't support.
        // In production with direct StackExchange.Redis, use SCAN to find all keys.
    }

    private void CleanupFallbackIfNeeded(bool enforceMax)
    {
        var nowTick = Environment.TickCount64;
        var intervalMs = (long)_fallbackCleanupInterval.TotalMilliseconds;
        var currentCount = _fallbackStore.Count;

        if (!enforceMax || currentCount <= MaxFallbackEntries)
        {
            var last = Interlocked.Read(ref _lastCleanupTick);
            if (nowTick - last < intervalMs)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _lastCleanupTick, nowTick, last) != last)
            {
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastCleanupTick, nowTick);
        }

        var now = DateTime.UtcNow;
        var expiredKeys = _fallbackExpiry
            .Where(kvp => kvp.Value <= now)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _fallbackExpiry.TryRemove(key, out _);
            _fallbackStore.TryRemove(key, out _);
        }

        var overflow = _fallbackStore.Count - MaxFallbackEntries;
        if (overflow <= 0)
        {
            return;
        }

        var oldestKeys = _fallbackExpiry
            .OrderBy(kvp => kvp.Value)
            .Take(overflow)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in oldestKeys)
        {
            _fallbackExpiry.TryRemove(key, out _);
            _fallbackStore.TryRemove(key, out _);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7620, LogLevel.Warning, "Redis {Operation} failed, using fallback")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);
    }
}

/// <summary>
/// JSON serialization context for Esri import types.
/// </summary>
[JsonSerializable(typeof(EsriImportProgress))]
[JsonSerializable(typeof(EsriImportRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class EsriImportJsonContext : JsonSerializerContext
{
}
