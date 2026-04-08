// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Import;

/// <summary>
/// Redis-based distributed import job manager with in-memory fallback.
/// Uses StackExchange.Redis primitives when available, falling back to IDistributedCache.
/// </summary>
internal sealed partial class RedisImportJobManager : IDistributedImportJobManager, IImportWorkerJobManager<GeoservicesImportRequest, GeoservicesImportProgress>, IImportCoordinationHealth, IDisposable
{
    private readonly ImportJobManagerState<GeoservicesImportRequest, GeoservicesImportProgress> _state;

    public RedisImportJobManager(
        IUniversalProgressStore universalProgressStore,
        IDistributedCache? distributedCache,
        ILogger<RedisImportJobManager> logger,
        IHostEnvironment hostEnvironment,
        IConnectionMultiplexer? redis = null)
    {
        _state = new ImportJobManagerState<GeoservicesImportRequest, GeoservicesImportProgress>(
            universalProgressStore,
            distributedCache,
            logger,
            hostEnvironment,
            redis,
            "geoservices:import:queue",
            "geoservices:import:leader",
            "geoservices:import:request:",
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

    public void Dispose()
    {
        if (_state.LeaderElection is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public interface IImportCoordinationHealth
{
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

        if (!_useRedis)
        {
            if (_allowFallback)
            {
                _hasLoggedUnavailable = true;
                Log.QueueUsingFallback(_logger, _queueKey);
            }
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
            catch (Exception ex)
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
                catch (Exception ex)
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
            catch (Exception ex)
            {
                MarkRedisUnavailable("length", ex);
            }
        }

        return _allowFallback ? fallbackLength : 0L;
    }

    public async Task CompleteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_useRedis && _redisDb != null)
        {
            try
            {
                await _redisDb.ListRemoveAsync(_processingQueueKey, jobId, 1).ConfigureAwait(false);
            }
            catch (Exception ex)
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
                RedisValue[] inFlight;
                do
                {
                    inFlight = await _redisDb.ListRangeAsync(_processingQueueKey, 0, 99).ConfigureAwait(false);
                    foreach (var jobValue in inFlight)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var jobId = jobValue.ToString();
                        if (string.IsNullOrWhiteSpace(jobId))
                        {
                            continue;
                        }

                        await _redisDb.ListRemoveAsync(_processingQueueKey, jobId, 1).ConfigureAwait(false);
                        await _redisDb.ListLeftPushAsync(_queueKey, jobId).ConfigureAwait(false);
                        Log.JobRequeued(_logger, jobId);
                    }
                }
                while (inFlight.Length > 0);

                return;
            }
            catch (Exception ex)
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
        catch (Exception ex)
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
/// Redis-based leader election using StackExchange.Redis locks.
/// Falls back to node-local leadership when Redis is unavailable so single-node
/// imports continue processing.
/// </summary>
internal sealed partial class RedisLeaderElection : IDistributedLeaderElection, IDisposable
{
    private readonly IDatabase? _redisDb;
    private readonly ILogger _logger;
    private readonly string _lockKey;
    private readonly string _instanceId;
    private readonly bool _allowLocalFallbackLeadership;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);
    private readonly Timer _heartbeatTimer;
    private static readonly TimeSpan _redisRetryInterval = TimeSpan.FromSeconds(30);
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private volatile bool _useRedis;
    private volatile bool _isLeader;
    private volatile bool _usingLocalFallbackLeadership;
    private volatile bool _disposed;

    public RedisLeaderElection(
        IConnectionMultiplexer? redis,
        ILogger logger,
        string lockKey,
        string instanceId,
        bool allowLocalFallbackLeadership = true)
    {
        _redisDb = redis?.GetDatabase();
        _useRedis = _redisDb != null;
        _logger = logger;
        _lockKey = lockKey;
        _instanceId = instanceId;
        _allowLocalFallbackLeadership = allowLocalFallbackLeadership;
        _heartbeatTimer = new Timer(HeartbeatCallback, null, Timeout.Infinite, Timeout.Infinite);

        if (!_useRedis && _allowLocalFallbackLeadership)
        {
            ActivateLocalFallbackLeadership();
        }
    }

    public bool IsLeader => _isLeader;
    public string InstanceId => _instanceId;
    internal bool IsUsingFallback => !_useRedis || _usingLocalFallbackLeadership;

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_useRedis && _redisDb != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!_useRedis || _redisDb == null)
        {
            if (_allowLocalFallbackLeadership)
            {
                ActivateLocalFallbackLeadership();
                return true;
            }

            _isLeader = false;
            return false;
        }

        if (_isLeader && !_usingLocalFallbackLeadership)
        {
            return true;
        }

        try
        {
            var acquired = await _redisDb.LockTakeAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
            if (acquired)
            {
                _isLeader = true;
                _usingLocalFallbackLeadership = false;
                _heartbeatTimer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                Log.LeadershipAcquired(_logger, _instanceId);
                return true;
            }

            _isLeader = false;
            _usingLocalFallbackLeadership = false;
            return false;
        }
        catch (Exception ex)
        {
            MarkRedisUnavailable("acquire", ex);
            return _isLeader;
        }
    }

    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_useRedis && _redisDb != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
            if (_useRedis && _redisDb != null && _isLeader)
            {
                await EnsureRedisLeadershipAsync().ConfigureAwait(false);
            }
        }

        if (!_useRedis || _redisDb == null)
        {
            if (_allowLocalFallbackLeadership)
            {
                ActivateLocalFallbackLeadership();
                return true;
            }

            _isLeader = false;
            return false;
        }

        if (!_isLeader)
        {
            return false;
        }

        try
        {
            var extended = await _redisDb.LockExtendAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
            if (!extended)
            {
                _isLeader = false;
                _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Log.LeadershipLost(_logger, _instanceId);
            }

            return _isLeader;
        }
        catch (Exception ex)
        {
            MarkRedisUnavailable("heartbeat", ex);
            return false;
        }
    }

    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);

        if (!_useRedis || _redisDb == null)
        {
            _isLeader = false;
            _usingLocalFallbackLeadership = false;
            return;
        }

        try
        {
            await _redisDb.LockReleaseAsync(_lockKey, _instanceId).ConfigureAwait(false);
            Log.LeadershipReleased(_logger, _instanceId);
        }
        catch (Exception ex)
        {
            Log.LeadershipError(_logger, "release", ex);
        }
        finally
        {
            _isLeader = false;
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
            if (_usingLocalFallbackLeadership)
            {
                _usingLocalFallbackLeadership = false;
                _isLeader = false;
                _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Log.RedisLeadershipRequired(_logger, _instanceId);
            }
        }
        catch (Exception ex)
        {
            _lastRedisFailure = DateTime.UtcNow;
            Log.LeadershipError(_logger, "restore", ex);
        }
    }

    private async Task EnsureRedisLeadershipAsync()
    {
        if (_redisDb == null || !_useRedis || !_isLeader)
        {
            return;
        }

        try
        {
            var extended = await _redisDb.LockExtendAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
            if (extended)
            {
                return;
            }

            var acquired = await _redisDb.LockTakeAsync(_lockKey, _instanceId, _leaseDuration).ConfigureAwait(false);
            if (!acquired)
            {
                _isLeader = false;
                _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
                Log.LeadershipLost(_logger, _instanceId);
            }
        }
        catch (Exception ex)
        {
            MarkRedisUnavailable("sync", ex);
        }
    }

    private void MarkRedisUnavailable(string operation, Exception ex)
    {
        _useRedis = false;
        _lastRedisFailure = DateTime.UtcNow;
        if (_allowLocalFallbackLeadership)
        {
            if (!_usingLocalFallbackLeadership)
            {
                ActivateLocalFallbackLeadership();
            }
        }
        else
        {
            _isLeader = false;
            _usingLocalFallbackLeadership = false;
            _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        }

        Log.LeadershipError(_logger, operation, ex);
    }

    private void ActivateLocalFallbackLeadership()
    {
        if (_usingLocalFallbackLeadership)
        {
            _isLeader = true;
            return;
        }

        _usingLocalFallbackLeadership = true;
        _isLeader = true;
        _heartbeatTimer.Change(Timeout.Infinite, Timeout.Infinite);
        Log.LocalFallbackLeadershipEnabled(_logger, _instanceId);
    }

    private void HeartbeatCallback(object? state)
    {
        if (_disposed)
        {
            return;
        }

        _ = HeartbeatAsync(CancellationToken.None);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _heartbeatTimer.Dispose();
    }

    private static partial class Log
    {
        [LoggerMessage(7610, LogLevel.Warning, "Redis leadership unavailable for instance {InstanceId}; using local fallback leadership")]
        public static partial void LocalFallbackLeadershipEnabled(ILogger logger, string instanceId);

        [LoggerMessage(7611, LogLevel.Information, "Instance {InstanceId} acquired leadership")]
        public static partial void LeadershipAcquired(ILogger logger, string instanceId);

        [LoggerMessage(7612, LogLevel.Warning, "Instance {InstanceId} lost leadership")]
        public static partial void LeadershipLost(ILogger logger, string instanceId);

        [LoggerMessage(7613, LogLevel.Information, "Instance {InstanceId} released leadership")]
        public static partial void LeadershipReleased(ILogger logger, string instanceId);

        [LoggerMessage(7614, LogLevel.Warning, "Leadership {Operation} error")]
        public static partial void LeadershipError(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7615, LogLevel.Information, "Redis connection restored for leadership election")]
        public static partial void RedisConnectionRestored(ILogger logger);

        [LoggerMessage(7616, LogLevel.Information, "Instance {InstanceId} switching from local fallback leadership to Redis lock leadership")]
        public static partial void RedisLeadershipRequired(ILogger logger, string instanceId);
    }
}

/// <summary>
/// Redis-based progress store using IDistributedCache.
/// </summary>
internal sealed partial class RedisProgressStore<T> : IDistributedProgressStore<T> where T : class
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

    public async Task SetProgressAsync(string jobId, T progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var key = $"{_keyPrefix}{jobId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

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

        try
        {
            var json = JsonSerializer.Serialize(progress, _jsonTypeInfo);
            await _cache.SetStringAsync(key, json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl },
                cancellationToken);
            if (_redis != null)
            {
                await _redis.GetDatabase().SetAddAsync(_activeIdsKey, jobId).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
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
            var json = await _cache.GetStringAsync(key, cancellationToken);
            if (json == null)
            {
                RemoveFallbackEntry(key);
                return null;
            }

            return JsonSerializer.Deserialize(json, _jsonTypeInfo);
        }
        catch (Exception ex)
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
                await _cache.RemoveAsync(key, cancellationToken);
                if (_redis != null)
                {
                    await _redis.GetDatabase().SetRemoveAsync(_activeIdsKey, jobId).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
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
        catch (Exception ex)
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
            catch (Exception ex)
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

            var json = await _cache!.GetStringAsync($"{_keyPrefix}{jobId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                await db.SetRemoveAsync(_activeIdsKey, jobId).ConfigureAwait(false);
                continue;
            }

            ids.Add(jobId);
        }

        return ids;
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
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class GeoservicesImportJsonContext : JsonSerializerContext
{
}
