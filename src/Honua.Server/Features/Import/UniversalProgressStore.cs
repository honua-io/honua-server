// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.Caching.Distributed;

namespace Honua.Server.Features.Import;

/// <summary>
/// Redis-based universal progress store with fallback to in-memory storage.
/// Supports any operation type implementing IOperationProgress.
/// </summary>
internal sealed partial class UniversalProgressStore : IUniversalProgressStore
{
    private readonly IDistributedCache? _cache;
    private readonly ILogger<UniversalProgressStore> _logger;
    private const string KeyPrefix = "universal:progress:";
    private const string TypePrefix = "universal:type:";
    private readonly ConcurrentDictionary<string, IOperationProgress> _fallbackStore = new();
    private readonly ConcurrentDictionary<string, DateTime> _fallbackExpiry = new();
    private readonly ConcurrentDictionary<string, OperationType> _typeStore = new();
    private const int MaxFallbackEntries = 5000;
    private static readonly TimeSpan _fallbackCleanupInterval = TimeSpan.FromMinutes(5);
    private long _lastCleanupTick = Environment.TickCount64;
    private volatile bool _isUsingFallback;

    public UniversalProgressStore(
        IDistributedCache? cache,
        ILogger<UniversalProgressStore> logger)
    {
        _cache = cache;
        _logger = logger;
        _isUsingFallback = cache == null;
    }

    public async Task SetProgressAsync(string operationId, IOperationProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default)
    {
        var key = $"{KeyPrefix}{operationId}";
        var typeKey = $"{TypePrefix}{operationId}";
        var effectiveTtl = ttl ?? TimeSpan.FromHours(24);

        if (_isUsingFallback || _cache == null)
        {
            _fallbackStore[key] = progress;
            _fallbackExpiry[key] = DateTime.UtcNow.Add(effectiveTtl);
            _typeStore[operationId] = progress.Type;
            CleanupFallbackIfNeeded(enforceMax: true);
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
            Log.RedisFailed(_logger, "set progress", ex);
            _isUsingFallback = true;
            _fallbackStore[key] = progress;
            _fallbackExpiry[key] = DateTime.UtcNow.Add(effectiveTtl);
            _typeStore[operationId] = progress.Type;
            CleanupFallbackIfNeeded(enforceMax: true);
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
                return null;

            return DeserializeProgress(json);
        }
        catch (Exception ex)
        {
            Log.RedisFailed(_logger, "get progress", ex);
            _isUsingFallback = true;
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

        if (_cache != null && !_isUsingFallback)
        {
            try
            {
                await _cache.RemoveAsync(key, cancellationToken);
                await _cache.RemoveAsync(typeKey, cancellationToken);
            }
            catch (Exception ex)
            {
                Log.RedisFailed(_logger, "delete progress", ex);
            }
        }
    }

    public Task<IReadOnlyList<string>> GetActiveOperationIdsAsync(OperationType? operationType = null, CancellationToken cancellationToken = default)
    {
        // For fallback, return in-memory keys
        CleanupFallbackIfNeeded(enforceMax: false);
        var now = DateTime.UtcNow;
        var activeIds = _fallbackStore.Keys
            .Where(k => k.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .Where(k => !_fallbackExpiry.TryGetValue(k, out var expiry) || expiry > now)
            .Select(k => k[KeyPrefix.Length..])
            .Where(id => operationType == null || (_typeStore.TryGetValue(id, out var type) && type == operationType))
            .ToList();

        return Task.FromResult<IReadOnlyList<string>>(activeIds);

        // Note: For Redis, we'd need SCAN which IDistributedCache doesn't support.
        // In production with direct StackExchange.Redis, use SCAN to find all keys.
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

    private string SerializeProgress(IOperationProgress progress)
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

    private IOperationProgress? DeserializeProgress(string json)
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

    private static partial class Log
    {
        [LoggerMessage(7650, LogLevel.Warning, "Redis {Operation} failed, using fallback")]
        public static partial void RedisFailed(ILogger logger, string operation, Exception exception);
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
        // We need to determine the operation type from TProgress to filter properly
        var operationType = GetOperationTypeForProgressType<TProgress>();
        return await _universalStore.GetActiveOperationIdsAsync(operationType, cancellationToken);
    }

    private static OperationType? GetOperationTypeForProgressType<T>()
        where T : class, IOperationProgress
    {
        // Map progress types to their corresponding operation types
        return typeof(T).Name switch
        {
            nameof(ImportProgress) => OperationType.Import,
            nameof(EsriImportProgress) => OperationType.ExternalImport,
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
[JsonSerializable(typeof(EsriImportProgress))]
[JsonSerializable(typeof(UploadProgress))]
[JsonSerializable(typeof(IngestProgress))]
[JsonSerializable(typeof(OperationType))]
[JsonSerializable(typeof(OperationStatus))]
[JsonSerializable(typeof(ImportStatus))]
[JsonSerializable(typeof(EsriImportStatus))]
internal sealed partial class UniversalProgressJsonContext : JsonSerializerContext
{
}
