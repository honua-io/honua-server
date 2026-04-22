// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Orchestration.Domain;
using Honua.Core.Features.Publishing.Domain;
using Honua.Core.Features.Raster.Domain;
using Honua.Server.Features.Infrastructure.Progress;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.StackExchangeRedis;
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
    private const string ActiveOperationIdsKey = "universal:progress:active";
    private const string ActiveOperationTypePrefix = "universal:progress:active:type:";
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
        var key = $"{KeyPrefix}{operationId}";
        var typeKey = $"{TypePrefix}{operationId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

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
            _ => throw new NotSupportedException($"Unsupported progress type '{progress.GetType().FullName}'.")
        };

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
        catch
        {
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
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(GeoservicesImportStatus))]
[JsonSerializable(typeof(GeoServerImportStatus))]
internal sealed partial class UniversalProgressJsonContext : JsonSerializerContext
{
}
