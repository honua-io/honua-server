// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Core.Features.Temporal.Domain;
using Honua.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using StackExchange.Redis;

namespace Honua.Migration;

/// <summary>
/// Redis-based universal progress store with fallback to in-memory storage.
/// Supports any operation type implementing IOperationProgress.
/// </summary>
internal sealed partial class UniversalProgressStore : IUniversalProgressStore
{
    private readonly IDistributedCache? _cache;
    private readonly IConnectionMultiplexer? _redis;
    private readonly ILogger<UniversalProgressStore> _logger;
    private const string KeyPrefix = "universal:progress:";
    private const string TypePrefix = "universal:type:";
    private const string HealthCheckKey = "universal:progress:health";
    private const string ActiveOperationIdsKey = "universal:progress:active";
    private const string ActiveOperationTypePrefix = "universal:progress:active:type:";
    private const string ConditionalWriteLockPrefix = "universal:progress:cas-lock:";
    private static readonly TimeSpan _conditionalWriteLockTtl = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan _conditionalWriteLockRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int ConditionalWriteLockMaxAttempts = 40;
    private const string ConditionalWriteLockReleaseScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    private readonly ConcurrentDictionary<string, IOperationProgress> _fallbackStore = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackExpiry = new();
    private readonly ConcurrentDictionary<string, OperationType> _typeStore = new();
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

    public UniversalProgressStore(
        IDistributedCache? cache,
        ILogger<UniversalProgressStore> logger,
        IConnectionMultiplexer? redis = null)
    {
        _cache = cache;
        _logger = logger;
        _redis = redis;
        _isUsingFallback = cache == null;
    }

    internal bool IsUsingFallback => _isUsingFallback;

    public async Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

        // Serialize terminal-status writes on the per-operation conditional-write lock so a worker's
        // Completed/Failed write cannot interleave between a concurrent compare-and-set's read and write
        // (for example an admin cancel) and be silently overwritten (honua-server#1593). Non-terminal
        // progress ticks stay on the cheap unguarded path.
        if (IsTerminalStatus(progress.Status) && !AllowsLocalFallback)
        {
            var lockToken = await TryAcquireConditionalWriteLockAsync(operationId, cancellationToken).ConfigureAwait(false);
            try
            {
                await SetProgressUnguardedAsync(operationId, progress, effectiveTtl, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await ReleaseConditionalWriteLockAsync(operationId, lockToken).ConfigureAwait(false);
            }

            return;
        }

        await SetProgressUnguardedAsync(operationId, progress, effectiveTtl, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProgressCompareAndSetResult> TrySetProgressAsync(
        string operationId,
        IOperationProgress progress,
        OperationStatus expectedStatus,
        TimeSpan? ttl = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

        if (AllowsLocalFallback)
        {
            return TrySetProgressInFallback(operationId, progress, expectedStatus, effectiveTtl);
        }

        if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_isUsingFallback)
        {
            throw CreateDistributedStateUnavailableException("conditionally set progress");
        }

        // Serialize competing conditional writers (and terminal-status plain writes) on a per-operation
        // Redis lock, then read-check-write. Lock acquisition is best-effort: when the lock cannot be
        // obtained the check-and-write still runs immediately before the write, which is the narrowest
        // window achievable over IDistributedCache.
        var lockToken = await TryAcquireConditionalWriteLockAsync(operationId, cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await GetProgressAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return ProgressCompareAndSetResult.NotFound;
            }

            if (current.Status != expectedStatus)
            {
                return ProgressCompareAndSetResult.StatusMismatch(current);
            }

            await SetProgressUnguardedAsync(operationId, progress, effectiveTtl, cancellationToken).ConfigureAwait(false);
            return ProgressCompareAndSetResult.Updated;
        }
        finally
        {
            await ReleaseConditionalWriteLockAsync(operationId, lockToken).ConfigureAwait(false);
        }
    }

    private async Task SetProgressUnguardedAsync(string operationId, IOperationProgress progress, TimeSpan effectiveTtl, CancellationToken cancellationToken)
    {
        var key = $"{KeyPrefix}{operationId}";
        var typeKey = $"{TypePrefix}{operationId}";

        if (AllowsLocalFallback)
        {
            CacheFallback(operationId, progress, effectiveTtl);
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
            var json = SerializeProgress(progress);
            await _cache.SetStringAsync(key, json,
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl },
                cancellationToken);

            // Store operation type for filtering
            await _cache.SetStringAsync(typeKey, progress.Type.ToString(),
                new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = effectiveTtl },
                cancellationToken);

            if (CanUseRedisBackplane)
            {
                await AddToRedisIndexAsync(operationId, progress.Type).ConfigureAwait(false);
            }
            else
            {
                CacheFallback(operationId, progress, effectiveTtl);
            }
        }
        catch (Exception ex)
        {
            HandleRedisFailure(ex, "set progress");
            throw CreateDistributedStateUnavailableException("set progress", ex);
        }
    }

    public async Task<TProgress?> GetProgressAsync<TProgress>(string operationId, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        var progress = await GetProgressAsync(operationId, cancellationToken);
        return progress as TProgress;
    }

    public async Task<IOperationProgress?> GetProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var key = $"{KeyPrefix}{operationId}";

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
                _typeStore.TryRemove(operationId, out _);
            }
            return null;
        }

        try
        {
            var json = await _cache.GetStringAsync(key, cancellationToken);
            if (json == null)
            {
                RemoveFallbackEntry(operationId, key);
                return null;
            }

            return DeserializeProgress(json);
        }
        catch (Exception ex)
        {
            HandleRedisFailure(ex, "get progress");
            throw CreateDistributedStateUnavailableException("get progress", ex);
        }
    }

    public async Task DeleteProgressAsync(string operationId, CancellationToken cancellationToken = default)
    {
        var key = $"{KeyPrefix}{operationId}";
        var typeKey = $"{TypePrefix}{operationId}";

        _fallbackStore.TryRemove(key, out _);
        _fallbackExpiry.TryRemove(key, out _);
        _typeStore.TryRemove(operationId, out _);

        if (_isUsingFallback && _cache != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_cache != null && !_isUsingFallback)
        {
            try
            {
                var storedType = await TryGetStoredOperationTypeAsync(operationId, cancellationToken).ConfigureAwait(false);
                await _cache.RemoveAsync(key, cancellationToken);
                await _cache.RemoveAsync(typeKey, cancellationToken);
                if (CanUseRedisBackplane)
                {
                    await RemoveFromRedisIndexAsync(operationId, storedType).ConfigureAwait(false);
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

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
    {
        return GetActiveOperationIdsInternalAsync(operationType, cancellationToken);
    }

    public async Task<IReadOnlyList<TProgress>> GetActiveOperationsAsync<TProgress>(OperationType operationType, CancellationToken cancellationToken = default)
        where TProgress : class, IOperationProgress
    {
        var operationIds = await GetActiveOperationIdsAsync(operationType, cancellationToken);
        var operations = new List<TProgress>();

        foreach (var operationId in operationIds)
        {
            var progress = await GetProgressAsync<TProgress>(operationId, cancellationToken);
            if (progress != null)
            {
                operations.Add(progress);
            }
        }

        return operations;
    }

    private static string SerializeProgress(IOperationProgress progress)
    {
        var wrapper = new ProgressWrapper
        {
            Type = progress.Type,
            ProgressType = progress.GetType().Name,
            Data = SerializeProgressData(progress)
        };

        return JsonSerializer.Serialize(wrapper, UniversalProgressJsonContext.Default.ProgressWrapper);
    }

    private static IOperationProgress? DeserializeProgress(string json)
    {
        var wrapper = JsonSerializer.Deserialize(json, UniversalProgressJsonContext.Default.ProgressWrapper);
        if (wrapper?.Data == null)
        {
            return null;
        }

        return wrapper.ProgressType switch
        {
            nameof(ImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.ImportProgress),
            nameof(GeoservicesImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoservicesImportProgress),
            nameof(GeoServerImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoServerImportProgress),
            nameof(UploadProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.UploadProgress),
            nameof(IngestProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.IngestProgress),
            nameof(TileOperationProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.TileOperationProgress),
            nameof(ExportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.ExportProgress),
            nameof(PrintProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.PrintProgress),
            nameof(RasterImportProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.RasterImportProgress),
            nameof(GeoprocessingProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.GeoprocessingProgress),
            nameof(PublishingProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.PublishingProgress),
            nameof(WorkflowProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.WorkflowProgress),
            nameof(DeploymentProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.DeploymentProgress),
            nameof(TemporalCorrectiveJobProgress) => wrapper.Data.Deserialize(UniversalProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            _ => null
        };
    }

    private static JsonElement SerializeProgressData(IOperationProgress progress)
        => progress switch
        {
            ImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.ImportProgress),
            GeoservicesImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoservicesImportProgress),
            GeoServerImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoServerImportProgress),
            UploadProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.UploadProgress),
            IngestProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.IngestProgress),
            TileOperationProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.TileOperationProgress),
            ExportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.ExportProgress),
            PrintProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.PrintProgress),
            RasterImportProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.RasterImportProgress),
            GeoprocessingProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.GeoprocessingProgress),
            PublishingProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.PublishingProgress),
            WorkflowProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.WorkflowProgress),
            DeploymentProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.DeploymentProgress),
            TemporalCorrectiveJobProgress value => JsonSerializer.SerializeToElement(value, UniversalProgressJsonContext.Default.TemporalCorrectiveJobProgress),
            _ => throw new NotSupportedException($"Unsupported progress type '{progress.GetType().FullName}'.")
        };

    private static bool IsTerminalStatus(OperationStatus status)
        => status is OperationStatus.Completed or OperationStatus.Failed or OperationStatus.Cancelled;

    /// <summary>
    /// Atomic compare-and-set against the in-memory fallback store: the status check and the swap are
    /// linearized through <see cref="ConcurrentDictionary{TKey,TValue}.TryUpdate"/>, retrying when a
    /// concurrent writer replaced the entry between the read and the swap.
    /// </summary>
    private ProgressCompareAndSetResult TrySetProgressInFallback(
        string operationId,
        IOperationProgress progress,
        OperationStatus expectedStatus,
        TimeSpan effectiveTtl)
    {
        var key = $"{KeyPrefix}{operationId}";

        while (true)
        {
            CleanupFallbackIfNeeded(enforceMax: false);

            if (!_fallbackStore.TryGetValue(key, out var current))
            {
                return ProgressCompareAndSetResult.NotFound;
            }

            if (_fallbackExpiry.TryGetValue(key, out var expiry) && expiry <= DateTime.UtcNow)
            {
                RemoveFallbackEntry(operationId, key);
                return ProgressCompareAndSetResult.NotFound;
            }

            if (current.Status != expectedStatus)
            {
                return ProgressCompareAndSetResult.StatusMismatch(current);
            }

            if (_fallbackStore.TryUpdate(key, progress, current))
            {
                _fallbackExpiry[key] = DateTime.UtcNow.Add(effectiveTtl);
                _typeStore[operationId] = progress.Type;
                CleanupFallbackIfNeeded(enforceMax: true);
                return ProgressCompareAndSetResult.Updated;
            }

            // Lost the swap race to a concurrent writer; re-read and re-evaluate the condition.
        }
    }

    /// <summary>
    /// Best-effort per-operation distributed lock (SET NX with TTL) used to serialize conditional writes
    /// and terminal-status writes across nodes. Returns the lock token, or null when no Redis backplane is
    /// available or the lock could not be acquired in time; callers proceed unguarded in that case.
    /// </summary>
    private async Task<string?> TryAcquireConditionalWriteLockAsync(string operationId, CancellationToken cancellationToken)
    {
        if (_redis is null || _isUsingFallback)
        {
            return null;
        }

        var lockKey = $"{ConditionalWriteLockPrefix}{operationId}";
        var token = Guid.NewGuid().ToString("N");

        try
        {
            var db = _redis.GetDatabase();
            for (var attempt = 0; attempt < ConditionalWriteLockMaxAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await db.StringSetAsync(lockKey, token, _conditionalWriteLockTtl, When.NotExists).ConfigureAwait(false))
                {
                    return token;
                }

                await Task.Delay(_conditionalWriteLockRetryDelay, cancellationToken).ConfigureAwait(false);
            }

            Log.ConditionalWriteLockUnavailable(_logger, operationId, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.ConditionalWriteLockUnavailable(_logger, operationId, ex);
        }

        return null;
    }

    private async Task ReleaseConditionalWriteLockAsync(string operationId, string? token)
    {
        if (token is null || _redis is null)
        {
            return;
        }

        try
        {
            var db = _redis.GetDatabase();

            // Compare-and-delete so a lock that expired and was re-acquired by another writer is not released.
            await db.ScriptEvaluateAsync(
                ConditionalWriteLockReleaseScript,
                [(RedisKey)$"{ConditionalWriteLockPrefix}{operationId}"],
                [(RedisValue)token]).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.ConditionalWriteLockReleaseFailed(_logger, operationId, ex);
        }
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

            // Extract operation ID from key and remove from type store
            if (key.StartsWith(KeyPrefix))
            {
                var operationId = key[KeyPrefix.Length..];
                _typeStore.TryRemove(operationId, out _);
            }
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

            // Extract operation ID from key and remove from type store
            if (key.StartsWith(KeyPrefix))
            {
                var operationId = key[KeyPrefix.Length..];
                _typeStore.TryRemove(operationId, out _);
            }
        }
    }

    private void CacheFallback(string operationId, IOperationProgress progress, TimeSpan ttl)
    {
        var key = $"{KeyPrefix}{operationId}";
        _fallbackStore[key] = progress;
        _fallbackExpiry[key] = DateTime.UtcNow.Add(ttl);
        _typeStore[operationId] = progress.Type;
        CleanupFallbackIfNeeded(enforceMax: true);
    }

    private void RemoveFallbackEntry(string operationId, string key)
    {
        _fallbackStore.TryRemove(key, out _);
        _fallbackExpiry.TryRemove(key, out _);
        _typeStore.TryRemove(operationId, out _);
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
            await _cache.GetStringAsync(HealthCheckKey, cancellationToken).ConfigureAwait(false);
            _isUsingFallback = false;
            Log.RedisConnectionRestored(_logger);
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
        => new($"Distributed import progress state is unavailable while attempting to {operation}.", innerException);

    private async Task<IReadOnlyList<string>> GetActiveOperationIdsInternalAsync(
        OperationType? operationType,
        CancellationToken cancellationToken)
    {
        if (_cache == null)
        {
            return GetActiveOperationIdsFromFallback(operationType);
        }

        if (_isUsingFallback && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CanUseRedisBackplane)
        {
            try
            {
                return await GetActiveOperationIdsFromRedisAsync(operationType, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex, "scan progress");
                throw CreateDistributedStateUnavailableException("list active operations", ex);
            }
        }

        if (!_isUsingFallback)
        {
            return GetActiveOperationIdsFromFallback(operationType);
        }

        throw CreateDistributedStateUnavailableException("list active operations");
    }

    private List<string> GetActiveOperationIdsFromFallback(OperationType? operationType)
    {
        CleanupFallbackIfNeeded(enforceMax: false);
        var now = DateTime.UtcNow;
        return _fallbackStore.Keys
            .Where(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Where(k => !_fallbackExpiry.TryGetValue(k, out var expiry) || expiry > now)
            .Select(k => k[KeyPrefix.Length..])
            .Where(id => operationType == null || (_typeStore.TryGetValue(id, out var type) && type == operationType))
            .ToList();
    }

    private async Task<IReadOnlyList<string>> GetActiveOperationIdsFromRedisAsync(
        OperationType? operationType,
        CancellationToken cancellationToken)
    {
        var db = _redis!.GetDatabase();
        var setKey = operationType.HasValue
            ? GetActiveOperationTypeKey(operationType.Value)
            : ActiveOperationIdsKey;
        var members = await db.SetMembersAsync(setKey).ConfigureAwait(false);
        var activeIds = new List<string>(members.Length);

        foreach (var member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operationId = member.ToString();
            if (string.IsNullOrWhiteSpace(operationId))
            {
                continue;
            }

            // Check if progress entry still exists
            var progressExists = await ProgressEntryExistsAsync(operationId, cancellationToken).ConfigureAwait(false);
            if (!progressExists)
            {
                await RemoveFromRedisIndexAsync(operationId, operationType).ConfigureAwait(false);
                continue;
            }

            activeIds.Add(operationId);
        }

        return activeIds;
    }

    private async Task<OperationType?> TryGetStoredOperationTypeAsync(string operationId, CancellationToken cancellationToken)
    {
        try
        {
            var typeValue = await _cache!.GetStringAsync($"{TypePrefix}{operationId}", cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(typeValue) &&
                Enum.TryParse<OperationType>(typeValue.ToString(), true, out var parsedType))
            {
                return parsedType;
            }

            var progressValue = await _cache!.GetStringAsync($"{KeyPrefix}{operationId}", cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(progressValue))
            {
                return null;
            }

            var progress = DeserializeProgress(progressValue.ToString());
            return progress?.Type;
        }
        catch (Exception ex)
        {
            Log.StoredOperationTypeLookupFailed(_logger, operationId, ex);
            return null;
        }
    }

    private async Task<bool> ProgressEntryExistsAsync(string operationId, CancellationToken cancellationToken)
    {
        var progressJson = await _cache!.GetStringAsync($"{KeyPrefix}{operationId}", cancellationToken).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(progressJson);
    }

    private async Task AddToRedisIndexAsync(string operationId, OperationType operationType)
    {
        var db = _redis!.GetDatabase();
        await db.SetAddAsync(ActiveOperationIdsKey, operationId).ConfigureAwait(false);
        await db.SetAddAsync(GetActiveOperationTypeKey(operationType), operationId).ConfigureAwait(false);
    }

    private async Task RemoveFromRedisIndexAsync(string operationId, OperationType? operationType)
    {
        var db = _redis!.GetDatabase();
        await db.SetRemoveAsync(ActiveOperationIdsKey, operationId).ConfigureAwait(false);
        if (operationType.HasValue)
        {
            await db.SetRemoveAsync(GetActiveOperationTypeKey(operationType.Value), operationId).ConfigureAwait(false);
        }
    }

    private static string GetActiveOperationTypeKey(OperationType operationType)
        => $"{ActiveOperationTypePrefix}{operationType.ToString().ToLowerInvariant()}";

    private static partial class Log
    {
        [LoggerMessage(7650, LogLevel.Warning, "Redis {Operation} failed for progress store")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7651, LogLevel.Information, "Redis connection restored for progress store")]
        public static partial void RedisConnectionRestored(ILogger logger);

        [LoggerMessage(7652, LogLevel.Warning, "Conditional-write lock for operation {OperationId} could not be acquired; proceeding without cross-node serialization")]
        public static partial void ConditionalWriteLockUnavailable(ILogger logger, string operationId, Exception? exception);

        [LoggerMessage(7653, LogLevel.Debug, "Failed to release conditional-write lock for operation {OperationId}")]
        public static partial void ConditionalWriteLockReleaseFailed(ILogger logger, string operationId, Exception exception);

        [LoggerMessage(7654, LogLevel.Debug, "Failed to resolve stored operation type for operation {OperationId}")]
        public static partial void StoredOperationTypeLookupFailed(ILogger logger, string operationId, Exception exception);
    }
}

/// <summary>
/// Adapter that implements IDistributedProgressStore using IUniversalProgressStore.
/// This allows existing code using typed progress stores to work with the unified system.
/// </summary>
/// <summary>
/// Adapter that implements IDistributedProgressStore using IUniversalProgressStore.
/// This allows existing code using typed progress stores to work with the unified system.
/// Note: This only works for types that implement IOperationProgress.
/// </summary>
internal sealed class DistributedProgressStoreAdapter<TProgress> : IDistributedProgressStore<TProgress>
    where TProgress : class, IOperationProgress
{
    private readonly IUniversalProgressStore _universalStore;

    public DistributedProgressStoreAdapter(IUniversalProgressStore universalStore)
    {
        _universalStore = universalStore;
    }

    public async Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        await _universalStore.SetProgressAsync(jobId, (Core.Features.Infrastructure.Domain.IOperationProgress)progress, ttl, cancellationToken);
    }

    public async Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return await _universalStore.GetProgressAsync<TProgress>(jobId, cancellationToken);
    }

    public async Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default)
    {
        await _universalStore.DeleteProgressAsync(jobId, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default)
    {
        var operationType = GetOperationTypeForProgressType<TProgress>();
        if (operationType is not { } resolvedOperationType)
        {
            return [];
        }

        var operations = await _universalStore.GetActiveOperationsAsync<TProgress>(resolvedOperationType, cancellationToken);
        return operations
            .Select(progress => progress.OperationId)
            .ToArray();
    }

    private static OperationType? GetOperationTypeForProgressType<T>()
        where T : class, IOperationProgress
    {
        // Map progress types to their corresponding operation types
        return typeof(T).Name switch
        {
            nameof(ImportProgress) => OperationType.Import,
            nameof(GeoservicesImportProgress) => OperationType.ExternalImport,
            nameof(GeoServerImportProgress) => OperationType.ExternalImport,
            nameof(TileOperationProgress) => OperationType.TileCache,
            nameof(ExportProgress) => OperationType.Export,
            nameof(PrintProgress) => OperationType.Print,
            nameof(GeoprocessingProgress) => OperationType.Geoprocessing,
            nameof(PublishingProgress) => OperationType.Publishing,
            nameof(WorkflowProgress) => OperationType.Orchestration,
            nameof(DeploymentProgress) => OperationType.Deployment,
            _ => null
        };
    }
}

/// <summary>
/// Wrapper for storing polymorphic progress data with type information.
/// </summary>
internal sealed record ProgressWrapper
{
    public required OperationType Type { get; init; }
    public required string ProgressType { get; init; }
    public required JsonElement Data { get; init; }
}

/// <summary>
/// JSON serialization context for universal progress storage.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(ProgressWrapper))]
[JsonSerializable(typeof(ImportProgress))]
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoServerImportProgress))]
[JsonSerializable(typeof(MigrationApplyExecutionArtifact))]
[JsonSerializable(typeof(MigrationApplyExecutionSummary))]
[JsonSerializable(typeof(MigrationApplyExecutionStepResult))]
[JsonSerializable(typeof(UploadProgress))]
[JsonSerializable(typeof(IngestProgress))]
[JsonSerializable(typeof(TileOperationProgress))]
[JsonSerializable(typeof(ExportProgress))]
[JsonSerializable(typeof(PrintProgress))]
[JsonSerializable(typeof(RasterImportProgress))]
[JsonSerializable(typeof(RasterImportPhase))]
[JsonSerializable(typeof(GeoprocessingProgress))]
[JsonSerializable(typeof(GeoprocessingWorkflowStatus))]
[JsonSerializable(typeof(GeoprocessingStageKind))]
[JsonSerializable(typeof(GeoprocessingStageStatus))]
[JsonSerializable(typeof(PublishingProgress))]
[JsonSerializable(typeof(PublishIntentStatus))]
[JsonSerializable(typeof(WorkflowProgress))]
[JsonSerializable(typeof(WorkflowRunStatus))]
[JsonSerializable(typeof(DeploymentProgress))]
[JsonSerializable(typeof(DeploymentStatus))]
[JsonSerializable(typeof(RolloutState))]
[JsonSerializable(typeof(TemporalCorrectiveJobProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(GeoservicesImportStatus))]
[JsonSerializable(typeof(GeoServerImportStatus))]
[JsonSerializable(typeof(Honua.Core.Features.Tiles.PMTiles.PMTilesArtifactDescriptor))]
[JsonSerializable(typeof(Honua.Core.Features.Tiles.PMTiles.PMTilesUrlStrategy))]
internal sealed partial class UniversalProgressJsonContext : JsonSerializerContext
{
}
