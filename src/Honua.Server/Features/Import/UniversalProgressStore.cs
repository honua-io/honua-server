// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using StackExchange.Redis;

namespace Honua.Server.Features.Import;

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
    private readonly ConcurrentDictionary<string, IOperationProgress> _fallbackStore = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackExpiry = new();
    private readonly ConcurrentDictionary<string, OperationType> _typeStore = new();
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan _fallbackCleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan _redisRetryInterval = TimeSpan.FromSeconds(30);
    private long _lastCleanupTick = Environment.TickCount64;
    private DateTime _lastRedisFailure = DateTime.MinValue;
    private volatile bool _isUsingFallback;

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
        var key = $"{KeyPrefix}{operationId}";
        var typeKey = $"{TypePrefix}{operationId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

        CacheFallback(operationId, progress, effectiveTtl);

        if (_isUsingFallback && _cache != null && ShouldRetryRedis(DateTime.UtcNow))
        {
            await TryRestoreRedisAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_isUsingFallback || _cache == null)
        {
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
        }
        catch (Exception ex)
        {
            HandleRedisFailure(ex, "set progress");
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
            return _fallbackStore.TryGetValue(key, out var progress) ? progress : null;
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
                await _cache.RemoveAsync(key, cancellationToken);
                await _cache.RemoveAsync(typeKey, cancellationToken);
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex, "delete progress");
            }
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
        // Create a type-aware wrapper for serialization
        var wrapper = new ProgressWrapper
        {
            Type = progress.Type,
            ProgressType = progress.GetType().Name,
            Data = progress
        };

        return JsonSerializer.Serialize(wrapper, UniversalProgressJsonContext.Default.ProgressWrapper);
    }

    private static IOperationProgress? DeserializeProgress(string json)
    {
        var wrapper = JsonSerializer.Deserialize(json, UniversalProgressJsonContext.Default.ProgressWrapper);
        if (wrapper?.Data == null)
            return null;

        // The JSON context handles polymorphic deserialization
        return wrapper.Data;
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

        if (!_isUsingFallback && _redis != null)
        {
            try
            {
                return await GetActiveOperationIdsFromRedisAsync(operationType, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleRedisFailure(ex, "scan progress");
            }
        }

        return GetActiveOperationIdsFromFallback(operationType);
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
        var ids = new List<string>();
        var db = _redis!.GetDatabase();
        var database = db.Database;
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            if (!server.IsConnected)
            {
                continue;
            }

            foreach (var key in server.Keys(database, pattern: $"{KeyPrefix}*"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var keyString = key.ToString();
                if (!keyString.StartsWith(KeyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var operationId = keyString[KeyPrefix.Length..];
                if (string.IsNullOrWhiteSpace(operationId))
                {
                    continue;
                }

                ids.Add(operationId);
            }
        }

        if (operationType == null)
        {
            return ids;
        }

        var filtered = new List<string>(ids.Count);
        foreach (var operationId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var typeMatches = await MatchesOperationTypeAsync(db, operationId, operationType.Value).ConfigureAwait(false);
            if (typeMatches)
            {
                filtered.Add(operationId);
            }
        }

        return filtered;
    }

    private static async Task<bool> MatchesOperationTypeAsync(IDatabase db, string operationId, OperationType expectedType)
    {
        try
        {
            var typeValue = await db.StringGetAsync($"{TypePrefix}{operationId}").ConfigureAwait(false);
            if (!typeValue.IsNullOrEmpty &&
                Enum.TryParse<OperationType>(typeValue.ToString(), true, out var parsedType))
            {
                return parsedType == expectedType;
            }

            var progressValue = await db.StringGetAsync($"{KeyPrefix}{operationId}").ConfigureAwait(false);
            if (progressValue.IsNullOrEmpty)
            {
                return false;
            }

            var progress = DeserializeProgress(progressValue.ToString());
            return progress?.Type == expectedType;
        }
        catch
        {
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(7650, LogLevel.Warning, "Redis {Operation} failed, using fallback")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);

        [LoggerMessage(7651, LogLevel.Information, "Redis connection restored for progress store")]
        public static partial void RedisConnectionRestored(ILogger logger);
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
            nameof(MigrationEvidenceProgress) => OperationType.MigrationEvidence,
            nameof(TileOperationProgress) => OperationType.TileCache,
            nameof(ExportProgress) => OperationType.Export,
            nameof(PrintProgress) => OperationType.Print,
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
    public required IOperationProgress Data { get; init; }
}

/// <summary>
/// JSON serialization context for universal progress storage.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.General)]
[JsonSerializable(typeof(ProgressWrapper))]
[JsonSerializable(typeof(IOperationProgress))]
[JsonSerializable(typeof(ImportProgress))]
[JsonSerializable(typeof(GeoservicesImportProgress))]
[JsonSerializable(typeof(GeoServerImportProgress))]
[JsonSerializable(typeof(MigrationEvidenceProgress))]
[JsonSerializable(typeof(UploadProgress))]
[JsonSerializable(typeof(IngestProgress))]
[JsonSerializable(typeof(TileOperationProgress))]
[JsonSerializable(typeof(ExportProgress))]
[JsonSerializable(typeof(PrintProgress))]
[JsonSerializable(typeof(RasterImportProgress))]
[JsonSerializable(typeof(RasterImportPhase))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(GeoservicesImportStatus))]
[JsonSerializable(typeof(GeoServerImportStatus))]
[JsonSerializable(typeof(MigrationEvidenceJobStatus))]
internal sealed partial class UniversalProgressJsonContext : JsonSerializerContext
{
}
