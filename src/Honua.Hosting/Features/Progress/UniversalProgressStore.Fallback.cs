// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Migration;

internal sealed partial class UniversalProgressStore
{
    private readonly ConcurrentDictionary<string, FallbackProgress> _fallbackStore = new();

    private sealed class FallbackProgress(IOperationProgress progress, DateTime expiresAt)
    {
        public IOperationProgress Progress { get; } = progress;
        public DateTime ExpiresAt { get; } = expiresAt;
    }

    private FallbackProgress? GetFallbackEntry(string key)
    {
        CleanupFallbackIfNeeded(enforceMax: false);
        while (_fallbackStore.TryGetValue(key, out var current))
        {
            if (current.ExpiresAt > DateTime.UtcNow)
            {
                return current;
            }

            if (RemoveFallbackSnapshot(new(key, current)))
            {
                return null;
            }
        }

        return null;
    }

    private ProgressCompareAndSetResult TryReplaceProgressInFallback(
        OperationProgressWrite write, IOperationProgress? expected, TimeSpan ttl)
    {
        var key = $"{KeyPrefix}{write.OperationId}";
        var current = GetFallbackEntry(key);
        if (current is null && expected is not null)
        {
            return ProgressCompareAndSetResult.NotFound;
        }

        if (current is not null && (expected is null || !OperationProgressSerializer.SameSnapshot(current.Progress, expected)))
        {
            return ProgressCompareAndSetResult.StatusMismatch(current.Progress);
        }

        var replacement = new FallbackProgress(write.Progress, DateTime.UtcNow.Add(ttl));
        var updated = current is null
            ? _fallbackStore.TryAdd(key, replacement)
            : _fallbackStore.TryUpdate(key, replacement, current);
        if (updated)
        {
            CleanupFallbackIfNeeded(enforceMax: true);
            return ProgressCompareAndSetResult.Updated;
        }

        current = GetFallbackEntry(key);
        return current is null ? ProgressCompareAndSetResult.NotFound : ProgressCompareAndSetResult.StatusMismatch(current.Progress);
    }

    private ProgressCompareAndSetResult TrySetProgressInFallback(
        OperationProgressWrite write, OperationStatus expectedStatus, TimeSpan ttl)
    {
        var key = $"{KeyPrefix}{write.OperationId}";
        while (true)
        {
            var current = GetFallbackEntry(key);
            if (current is null)
            {
                return ProgressCompareAndSetResult.NotFound;
            }

            if (current.Progress.Status != expectedStatus)
            {
                return ProgressCompareAndSetResult.StatusMismatch(current.Progress);
            }

            if (_fallbackStore.TryUpdate(key, new FallbackProgress(write.Progress, DateTime.UtcNow.Add(ttl)), current))
            {
                CleanupFallbackIfNeeded(enforceMax: true);
                return ProgressCompareAndSetResult.Updated;
            }
        }
    }

    private void CleanupFallbackIfNeeded(bool enforceMax)
    {
        var nowTick = Environment.TickCount64;
        var intervalMs = (long)_fallbackCleanupInterval.TotalMilliseconds;
        if (!enforceMax || _fallbackStore.Count <= MaxFallbackEntries)
        {
            var last = Interlocked.Read(ref _lastCleanupTick);
            if (nowTick - last < intervalMs || Interlocked.CompareExchange(ref _lastCleanupTick, nowTick, last) != last)
            {
                return;
            }
        }
        else
        {
            Interlocked.Exchange(ref _lastCleanupTick, nowTick);
        }

        var now = DateTime.UtcNow;
        foreach (var entry in _fallbackStore.Where(entry => entry.Value.ExpiresAt <= now))
        {
            RemoveFallbackSnapshot(entry);
        }

        var overflow = _fallbackStore.Count - MaxFallbackEntries;
        if (overflow > 0)
        {
            foreach (var entry in _fallbackStore.OrderBy(entry => entry.Value.ExpiresAt).Take(overflow))
            {
                RemoveFallbackSnapshot(entry);
            }
        }
    }

    private bool RemoveFallbackSnapshot(KeyValuePair<string, FallbackProgress> entry)
        => ((ICollection<KeyValuePair<string, FallbackProgress>>)_fallbackStore).Remove(entry);

    private void CacheFallback(string operationId, IOperationProgress progress, TimeSpan ttl)
    {
        _fallbackStore[$"{KeyPrefix}{operationId}"] = new FallbackProgress(progress, DateTime.UtcNow.Add(ttl));
        CleanupFallbackIfNeeded(enforceMax: true);
    }

    private void RemoveFallbackEntry(string key)
        => _fallbackStore.TryRemove(key, out _);

    private List<string> GetActiveOperationIdsFromFallback(OperationType? operationType)
    {
        CleanupFallbackIfNeeded(enforceMax: false);
        var now = DateTime.UtcNow;
        return _fallbackStore
            .Where(entry => entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal) && entry.Value.ExpiresAt > now)
            .Where(entry => operationType == null || entry.Value.Progress.Type == operationType)
            .Select(entry => entry.Key[KeyPrefix.Length..])
            .ToList();
    }
}
