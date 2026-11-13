// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Honua.Migration;

/// <summary>
/// Redis-based distributed import job manager with in-memory fallback.
/// Uses StackExchange.Redis primitives when available, falling back to IDistributedCache.
/// </summary>
// Note: the broad `catch (Exception ex)` clauses throughout this file (and the RedisJobQueue and
// RedisProgressStore<T> types below) are intentional. Redis is a best-effort dependency here: every
// catch marks the affected component unavailable, logs via Log.RedisFailed, and falls back to
// local/in-memory coordination rather than letting a transient Redis fault take down import processing.
internal sealed partial class RedisImportJobManager : IDistributedImportJobManager, IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress>, IImportCoordinationHealth
{
    private readonly ImportJobManagerState<GeoservicesImportRequest, GeoservicesImportProgress> _state;

    public RedisImportJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<RedisImportJobManager> logger,
        IHostEnvironment hostEnvironment,
        ImportCoordinationStores? stores = null)
    {
        _state = new ImportJobManagerState<GeoservicesImportRequest, GeoservicesImportProgress>(
            new ImportJobManagerContext(universalProgressStore, distributedCache, logger, hostEnvironment, stores ?? new ImportCoordinationStores()),
            new ImportJobKeys("geoservices:import:queue", "geoservices:import:leader", "geoservices:import:request:"),
            GeoservicesImportJsonContext.Default.GeoservicesImportRequest);
    }

    public IDistributedJobQueueService JobQueue => _state.JobQueue;
    public IDistributedLeaderElection LeaderElection => _state.LeaderElection;
    public IDistributedProgressStore<GeoservicesImportProgress> ProgressStore => _state.ProgressStore;
    public IDistributedProgressStore<GeoservicesImportRequest> RequestStore => _state.RequestStore;
    internal bool IsClusterDurable => _state.IsClusterDurable;
    public bool CanAcceptNewJobs => _state.CanAcceptNewJobs;
    internal static bool RequiresStrictDistributedMode(IHostEnvironment hostEnvironment)
        => !hostEnvironment.IsDevelopment() && !hostEnvironment.IsEnvironment("Test");
}

/// <summary>
/// Reports whether the distributed import coordination subsystem is healthy
/// enough to accept new import jobs.
/// </summary>
public interface IImportCoordinationHealth
{
    /// <summary>
    /// Gets a value indicating whether the coordinator can currently accept new
    /// import jobs (for example, when durable Redis-backed coordination is available).
    /// </summary>
    bool CanAcceptNewJobs { get; }
}

/// <summary>
/// Redis-based job queue using StackExchange.Redis lists.
/// Falls back to an in-memory queue when Redis is unavailable.
/// </summary>
internal sealed partial class RedisJobQueue : IDistributedJobQueueService
{
    private const string StrictModeUnavailableMessage = "Distributed import queue is unavailable while durable coordination is required.";
    private readonly IDatabase? _redisDb;
    private readonly ILogger _logger;
    private readonly string _queueKey;
    private readonly string _processingQueueKey;
    private readonly bool _allowFallback;
    private readonly ConcurrentQueue<string> _fallbackQueue = new();
    private readonly ConcurrentDictionary<string, byte> _fallbackInFlight = new(StringComparer.Ordinal);
    private static readonly TimeSpan _redisRetryInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private long _fallbackQueueLength;
    private volatile bool _useRedis;
    private volatile bool _hasLoggedUnavailable;

    public RedisJobQueue(IConnectionMultiplexer? redis, ILogger logger, string queueKey, bool allowFallback = true)
    {
        _redisDb = redis?.GetDatabase();
        _logger = logger;
        _queueKey = queueKey;
        _processingQueueKey = $"{queueKey}:processing";
        _allowFallback = allowFallback;
        _useRedis = _redisDb != null;

        if (!_useRedis && _allowFallback)
        {
            _hasLoggedUnavailable = true;
            Log.QueueUsingFallback(_logger, _queueKey);
        }
    }

    internal bool IsUsingFallback => !_useRedis;

    public async Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        if (!_useRedis && _redisDb != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_useRedis && _redisDb != null)
        {
            try
            {
                await _redisDb.ListLeftPushAsync(_queueKey, jobId).ConfigureAwait(false);
                Log.JobEnqueued(_logger, jobId);
                return;
            }
            // Intentionally generic (see class remarks): mark Redis unavailable and fall
            // back to the in-memory queue rather than failing the enqueue.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MarkRedisUnavailable("enqueue", ex);
            }
        }

        CacheFallbackJob(jobId);
    }

    public async Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.Add(timeout);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            if (!_useRedis && _redisDb != null && ShouldRetryRedis(DateTime.UtcNow))
            {
                await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            }

            if (_useRedis && _redisDb != null)
            {
                try
                {
                    var jobValue = await _redisDb.ListRightPopLeftPushAsync(_queueKey, _processingQueueKey).ConfigureAwait(false);
                    if (jobValue.HasValue)
                    {
                        var jobId = jobValue.ToString();
                        Log.JobDequeued(_logger, jobId);
                        return jobId;
                    }
                }
                // Intentionally generic (see class remarks): mark Redis unavailable and fall
                // back to in-memory/local dequeue rather than failing the poll.
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    MarkRedisUnavailable("dequeue", ex);
                }
            }

            if (_allowFallback && TryDequeueFallbackJob(out var fallbackJob))
            {
                Log.JobDequeuedFallback(_logger, fallbackJob);
                return fallbackJob;
            }

            // Wait before polling again
            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    public async Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default)
    {
        if (!_useRedis && _redisDb != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        var fallbackLength = Volatile.Read(ref _fallbackQueueLength);
        if (_useRedis && _redisDb != null)
        {
            try
            {
                return (long)await _redisDb.ListLengthAsync(_queueKey).ConfigureAwait(false) + fallbackLength;
            }
            // Intentionally generic (see class remarks): mark Redis unavailable and report
            // the local fallback length instead of failing the length check.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MarkRedisUnavailable("length", ex);
            }
        }

        return _allowFallback ? fallbackLength : 0L;
    }

    public async Task CompleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Completion is not a poll. The 30-second retry throttle must not leave a
        // finished job in the processing list, or in-flight recovery redelivers it
        // and repeats catalog mutations (#4877).
        if (!_useRedis && _redisDb != null)
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_useRedis && _redisDb != null)
        {
            try
            {
                await _redisDb.ListRemoveAsync(_processingQueueKey, jobId, 1).ConfigureAwait(false);
            }
            // Intentionally generic (see class remarks): completion bookkeeping is best-effort;
            // mark Redis unavailable rather than failing the caller's completion signal.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MarkRedisUnavailable("complete", ex);
            }
        }

        if (_allowFallback)
        {
            _fallbackInFlight.TryRemove(jobId, out _);
        }
    }

    public async Task RecoverInFlightAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_useRedis && _redisDb != null)
        {
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Atomically move a job back to the ready queue so a crash between
                    // "remove from processing" and "push to ready" cannot lose work.
                    var jobValue = await _redisDb
                        .ListRightPopLeftPushAsync(_processingQueueKey, _queueKey)
                        .ConfigureAwait(false);
                    if (!jobValue.HasValue)
                    {
                        break;
                    }

                    var jobId = jobValue.ToString();
                    if (!string.IsNullOrWhiteSpace(jobId))
                    {
                        Log.JobRequeued(_logger, jobId);
                    }
                }

                return;
            }
            // Intentionally generic (see class remarks): mark Redis unavailable and fall back
            // to recovering in-flight jobs from local state instead of failing recovery.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                MarkRedisUnavailable("recover", ex);
            }
        }

        if (!_allowFallback)
        {
            return;
        }

        foreach (var jobId in _fallbackInFlight.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_fallbackInFlight.TryRemove(jobId, out _))
            {
                _fallbackQueue.Enqueue(jobId);
                Interlocked.Increment(ref _fallbackQueueLength);
                Log.JobRequeuedFallback(_logger, jobId);
            }
        }
    }

    private bool ShouldRetryRedis(DateTime now)
    {
        return now - _lastRedisFailure > _redisRetryInterval;
    }

    private async Task TryRestoreRedisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_redisDb == null)
        {
            return;
        }

        try
        {
            await _redisDb.PingAsync().ConfigureAwait(false);
            if (!_useRedis)
            {
                Log.RedisConnectionRestored(_logger);
            }
            _useRedis = true;
            _hasLoggedUnavailable = false;
        }
        // Intentionally generic (see class remarks): this is a connection probe; any failure
        // just means Redis is still down, so keep using the local fallback and retry later.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastRedisFailure = DateTime.UtcNow;
            Log.RedisFailed(_logger, "restore", ex);
        }
    }

    private void MarkRedisUnavailable(string operation, Exception ex)
    {
        _useRedis = false;
        _lastRedisFailure = DateTime.UtcNow;
        _hasLoggedUnavailable = false;
        Log.RedisFailed(_logger, operation, ex);
    }

    private void CacheFallbackJob(string jobId)
    {
        if (!_allowFallback)
        {
            throw new InvalidOperationException(StrictModeUnavailableMessage);
        }

        _fallbackQueue.Enqueue(jobId);
        Interlocked.Increment(ref _fallbackQueueLength);
        if (!_hasLoggedUnavailable)
        {
            _hasLoggedUnavailable = true;
            Log.QueueUsingFallback(_logger, _queueKey);
        }

        Log.JobEnqueuedFallback(_logger, jobId);
    }

    private bool TryDequeueFallbackJob(out string jobId)
    {
        if (_fallbackQueue.TryDequeue(out var queued))
        {
            Interlocked.Decrement(ref _fallbackQueueLength);
            _fallbackInFlight[queued] = 0;
            jobId = queued;
            return true;
        }

        jobId = string.Empty;
        return false;
    }

    private static partial class Log
    {
        [LoggerMessage(7600, LogLevel.Warning, "Redis unavailable for distributed import queue (queue key: {QueueKey}); using in-memory fallback")]
        public static partial void QueueUsingFallback(ILogger logger, string queueKey);

        [LoggerMessage(7601, LogLevel.Debug, "Job {JobId} enqueued to Redis queue")]
        public static partial void JobEnqueued(ILogger logger, string jobId);

        [LoggerMessage(7602, LogLevel.Debug, "Job {JobId} dequeued from Redis queue")]
        public static partial void JobDequeued(ILogger logger, string jobId);

        [LoggerMessage(7603, LogLevel.Warning, "Redis {Operation} failed for distributed import queue")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7604, LogLevel.Information, "Redis connection restored for job queue")]
        public static partial void RedisConnectionRestored(ILogger logger);

        [LoggerMessage(7605, LogLevel.Debug, "Job {JobId} enqueued to in-memory fallback queue")]
        public static partial void JobEnqueuedFallback(ILogger logger, string jobId);

        [LoggerMessage(7606, LogLevel.Debug, "Job {JobId} dequeued from in-memory fallback queue")]
        public static partial void JobDequeuedFallback(ILogger logger, string jobId);

        [LoggerMessage(7607, LogLevel.Warning, "Job {JobId} requeued after incomplete processing was recovered")]
        public static partial void JobRequeued(ILogger logger, string jobId);

        [LoggerMessage(7608, LogLevel.Warning, "Job {JobId} requeued from in-memory fallback in-flight state")]
        public static partial void JobRequeuedFallback(ILogger logger, string jobId);
    }
}

/// <summary>
/// Redis-based progress store using IDistributedCache.
/// </summary>
internal sealed partial class RedisProgressStore<T> : IDistributedProgressStore<T>, IProgressStoreRecovery where T : class
{
    private const string ActiveIdsKeySuffix = "__active";
    private readonly IDistributedCache? _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger _logger;
    private readonly string _keyPrefix;
    private readonly string _healthCheckKey;
    private readonly string _activeIdsKey;
    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private readonly ConcurrentDictionary<string, T> _fallbackStore = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackExpiry = new();
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan _fallbackCleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _redisRetryInterval = TimeSpan.FromSeconds(30);
    private long _lastCleanupTick = Environment.TickCount64;
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private volatile bool _isUsingFallback;
    private bool AllowsLocalFallback => _cache == null;
    private bool CanUseRedisBackplane =>
        _redis != null &&
        _cache is RedisCache &&
        !_isUsingFallback;
    private bool CanUseRedisDirectly =>
        CanUseRedisBackplane &&
        _cache is not MemoryDistributedCache;

    public RedisProgressStore(
        IDistributedCache? cache,
        ILogger logger,
        string keyPrefix,
        JsonTypeInfo<T> jsonTypeInfo,
        IConnectionMultiplexer? redis = null)
    {
        _cache = cache;
        _logger = logger;
        _keyPrefix = keyPrefix;
        _jsonTypeInfo = jsonTypeInfo;
        _redis = redis;
        _healthCheckKey = $"{_keyPrefix}__health_check__";
        _activeIdsKey = $"{_keyPrefix}{ActiveIdsKeySuffix}";
        _isUsingFallback = cache == null;
    }

    internal bool IsUsingFallback => _isUsingFallback;

    public async Task ProbeRecoveryAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetProgressAsync(string jobId, T progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);
        var cachedWriteCompleted = false;

        if (AllowsLocalFallback)
        {
            CacheFallback(key, progress, effectiveTtl);
        }

        if (_isUsingFallback && _cache != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_isUsingFallback || _cache == null)
        {
            if (_cache != null)
            {
                throw CreateDistributedStateUnavailableException("set progress");
            }

            return;
        }

        // Captured to a local: the guard above already proved the cache is live, and a local
        // keeps that narrowing valid across the awaits and the catch block below (unlike the
        // nullable field, whose narrowing the compiler cannot carry across a try/catch).
        var cache = _cache;

        try
        {
            if (CanUseRedisDirectly)
            {
                await SetProgressDirectAsync(jobId, progress, effectiveTtl, cancellationToken).ConfigureAwait(false);
                return;
            }

            var json = JsonSerializer.Serialize(progress, _jsonTypeInfo);
            await cache.SetStringAsync(key, json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl },
                cancellationToken);
            cachedWriteCompleted = true;
            if (_redis != null)
            {
                await _redis!.GetDatabase().SetAddAsync(_activeIdsKey, jobId).ConfigureAwait(false);
            }
        }
        // Intentionally generic (see class remarks): translate any distributed-cache/Redis
        // failure into a well-defined distributed-state-unavailable exception and mark the
        // local fallback active.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (cachedWriteCompleted)
            {
                // Intentionally generic: best-effort rollback of the partially-written cache
                // entry; any failure here must not mask the original failure propagated below.
                try
                {
                    await cache.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
                {
                    Log.RedisFailed(_logger, "rollback progress", caughtException);
                }
            }

            HandleRedisFailure(ex, "set progress");
            throw CreateDistributedStateUnavailableException("set progress", ex);
        }
    }

    public async Task<T?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";

        if (_isUsingFallback && _cache != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_isUsingFallback || _cache == null)
        {
            if (_cache != null)
            {
                throw CreateDistributedStateUnavailableException("get progress");
            }

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
            if (CanUseRedisDirectly)
            {
                return await GetProgressDirectAsync(key, cancellationToken).ConfigureAwait(false);
            }

            var json = await _cache.GetStringAsync(key, cancellationToken);
            if (json == null)
            {
                RemoveFallbackEntry(key);
                return null;
            }

            return JsonSerializer.Deserialize(json, _jsonTypeInfo);
        }
        // Intentionally generic (see class remarks): translate any distributed-cache/Redis
        // failure into a well-defined distributed-state-unavailable exception and mark the
        // local fallback active.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            HandleRedisFailure(ex, "get progress");
            throw CreateDistributedStateUnavailableException("get progress", ex);
        }
    }

    public async Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";

        _fallbackStore.TryRemove(key, out _);
        _fallbackExpiry.TryRemove(key, out _);

        if (_isUsingFallback && _cache != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_cache != null && !_isUsingFallback)
        {
            try
            {
                if (CanUseRedisDirectly)
                {
                    await DeleteProgressDirectAsync(jobId, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    if (_redis != null)
                    {
                        await _redis!.GetDatabase().SetRemoveAsync(_activeIdsKey, jobId).ConfigureAwait(false);
                    }

                    await _cache.RemoveAsync(key, cancellationToken);
                }
            }
            // Intentionally generic (see class remarks): translate any distributed-cache/Redis
            // failure into a well-defined distributed-state-unavailable exception and mark the
            // local fallback active.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandleRedisFailure(ex, "delete progress");
                throw CreateDistributedStateUnavailableException("delete progress", ex);
            }
        }
        else if (_cache != null)
        {
            throw CreateDistributedStateUnavailableException("delete progress");
        }
    }

    public Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
    {
        return GetActiveJobIdsInternalAsync(cancellationToken);
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

    private void CacheFallback(string key, T progress, TimeSpan ttl)
    {
        _fallbackStore[key] = progress;
        _fallbackExpiry[key] = DateTime.UtcNow.Add(ttl);
        CleanupFallbackIfNeeded(enforceMax: true);
    }

    private void RemoveFallbackEntry(string key)
    {
        _fallbackStore.TryRemove(key, out _);
        _fallbackExpiry.TryRemove(key, out _);
    }

    private bool ShouldRetryRedis(DateTime now)
    {
        return _cache != null && now - _lastRedisFailure > _redisRetryInterval;
    }

    private async Task<bool> TryRestoreRedisAsync(CancellationToken cancellationToken)
    {
        if (_cache == null)
        {
            return false;
        }

        try
        {
            await _cache.GetStringAsync(_healthCheckKey, cancellationToken).ConfigureAwait(false);
            _isUsingFallback = false;
            Log.RedisRestored(_logger);
            return true;
        }
        // Intentionally generic (see class remarks): this is a connection probe; any failure
        // just means Redis is still down, so keep using the local fallback and retry later.
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _lastRedisFailure = DateTime.UtcNow;
            Log.RedisFailed(_logger, "restore progress", ex);
            return false;
        }
    }

    private void HandleRedisFailure(Exception ex, string operation)
    {
        _lastRedisFailure = DateTime.UtcNow;
        if (!_isUsingFallback)
        {
            _isUsingFallback = true;
        }

        Log.RedisFailed(_logger, operation, ex);
    }

    private static InvalidOperationException CreateDistributedStateUnavailableException(string operation, Exception? innerException = null)
        => new($"Distributed import state is unavailable while attempting to {operation}.", innerException);

    private async Task<IReadOnlyList<string>> GetActiveJobIdsInternalAsync(CancellationToken cancellationToken)
    {
        if (_cache == null)
        {
            return GetActiveJobIdsFromFallback();
        }

        if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_isUsingFallback && _redis != null)
        {
            try
            {
                return await GetActiveJobIdsFromRedisAsync(cancellationToken).ConfigureAwait(false);
            }
            // Intentionally generic (see class remarks): translate any distributed-cache/Redis
            // failure into a well-defined distributed-state-unavailable exception and mark the
            // local fallback active.
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                HandleRedisFailure(ex, "scan progress");
                throw CreateDistributedStateUnavailableException("list active jobs", ex);
            }
        }

        throw CreateDistributedStateUnavailableException("list active jobs");
    }

    private List<string> GetActiveJobIdsFromFallback()
    {
        CleanupFallbackIfNeeded(enforceMax: false);
        var now = DateTime.UtcNow;
        return _fallbackStore.Keys
            .Where(k => k.StartsWith(_keyPrefix, StringComparison.Ordinal))
            .Where(k => !_fallbackExpiry.TryGetValue(k, out var expiry) || expiry > now)
            .Select(k => k[_keyPrefix.Length..])
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetActiveJobIdsFromRedisAsync(CancellationToken cancellationToken)
    {
        var db = _redis!.GetDatabase();
        var members = await db.SetMembersAsync(_activeIdsKey).ConfigureAwait(false);
        var ids = new List<string>(members.Length);

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobId = member.ToString();
            if (string.IsNullOrWhiteSpace(jobId))
            {
                continue;
            }

            var payload = await db.StringGetAsync($"{_keyPrefix}{jobId}").ConfigureAwait(false);
            if (!payload.HasValue || string.IsNullOrWhiteSpace(payload.ToString()))
            {
                await db.SetRemoveAsync(_activeIdsKey, jobId).ConfigureAwait(false);
                continue;
            }

            ids.Add(jobId);
        }

        return ids;
    }

    private async Task SetProgressDirectAsync(string jobId, T progress, TimeSpan effectiveTtl, CancellationToken cancellationToken)
    {
        var key = $"{_keyPrefix}{jobId}";
        var db = _redis!.GetDatabase();
        var transaction = db.CreateTransaction();

        var json = JsonSerializer.Serialize(progress, _jsonTypeInfo);
        var writeTask = transaction.StringSetAsync(key, json, effectiveTtl);
        var indexTask = transaction.SetAddAsync(_activeIdsKey, jobId);

        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw CreateDistributedStateUnavailableException("set progress");
        }

        await Task.WhenAll(writeTask, indexTask).ConfigureAwait(false);
    }

    private async Task<T?> GetProgressDirectAsync(string key, CancellationToken cancellationToken)
    {
        var db = _redis!.GetDatabase();
        var payload = await db.StringGetAsync(key).ConfigureAwait(false);
        if (!payload.HasValue)
        {
            return default;
        }

        return JsonSerializer.Deserialize(payload.ToString(), _jsonTypeInfo);
    }

    private async Task DeleteProgressDirectAsync(string jobId, CancellationToken cancellationToken)
    {
        var key = $"{_keyPrefix}{jobId}";
        var db = _redis!.GetDatabase();
        var transaction = db.CreateTransaction();

        var removeTask = transaction.KeyDeleteAsync(key);
        var indexTask = transaction.SetRemoveAsync(_activeIdsKey, jobId);
        var committed = await transaction.ExecuteAsync().ConfigureAwait(false);
        if (!committed)
        {
            throw CreateDistributedStateUnavailableException("delete progress");
        }

        await Task.WhenAll(removeTask, indexTask).ConfigureAwait(false);
    }

    private static partial class Log
    {
        [LoggerMessage(7620, LogLevel.Warning, "Redis {Operation} failed for request/progress store")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7621, LogLevel.Information, "Redis connection restored for progress store")]
        public static partial void RedisRestored(ILogger logger);
    }
}

/// <summary>
/// JSON serialization context for Geoservices import types.
/// </summary>
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoservicesImportRequest))]
[JsonSerializable(typeof(GeoservicesCredentialDescriptor))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class GeoservicesImportJsonContext : JsonSerializerContext
{
}
