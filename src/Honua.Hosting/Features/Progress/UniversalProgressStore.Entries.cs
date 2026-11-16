// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Progress;

namespace Honua.Migration;

internal sealed partial class UniversalProgressStore
{
    private readonly ConcurrentDictionary<string, ProgressEntry> _entries = new();

    private sealed class ProgressEntry(IOperationProgress progress, DateTime expiresAt)
    {
        public IOperationProgress Progress { get; } = progress;
        public DateTime ExpiresAt { get; } = expiresAt;
    }

    private ProgressEntry? GetEntry(string key)
    {
        CleanupPeriodically();
        while (_entries.TryGetValue(key, out var current))
        {
            if (current.ExpiresAt > DateTime.UtcNow)
            {
                return current;
            }

            if (RemoveSnapshot(new(key, current)))
            {
                return null;
            }
        }

        return null;
    }

    private ProgressCompareAndSetResult TryReplaceProgress(
        OperationProgressWrite write, IOperationProgress? expected, TimeSpan ttl)
    {
        var key = $"{KeyPrefix}{write.OperationId}";
        var current = GetEntry(key);
        if (current is null && expected is not null)
        {
            return ProgressCompareAndSetResult.NotFound;
        }

        if (current is not null && (expected is null || !OperationProgressSerializer.SameSnapshot(current.Progress, expected)))
        {
            return ProgressCompareAndSetResult.StatusMismatch(current.Progress);
        }

        var replacement = new ProgressEntry(write.Progress, DateTime.UtcNow.Add(ttl));
        var updated = current is null
            ? _entries.TryAdd(key, replacement)
            : _entries.TryUpdate(key, replacement, current);
        if (updated)
        {
            EnforceCapacity();
            return ProgressCompareAndSetResult.Updated;
        }

        current = GetEntry(key);
        return current is null ? ProgressCompareAndSetResult.NotFound : ProgressCompareAndSetResult.StatusMismatch(current.Progress);
    }

    private ProgressCompareAndSetResult TrySetProgress(
        OperationProgressWrite write, OperationStatus expectedStatus, TimeSpan ttl)
    {
        var key = $"{KeyPrefix}{write.OperationId}";
        while (true)
        {
            var current = GetEntry(key);
            if (current is null)
            {
                return ProgressCompareAndSetResult.NotFound;
            }

            if (current.Progress.Status != expectedStatus)
            {
                return ProgressCompareAndSetResult.StatusMismatch(current.Progress);
            }

            if (_entries.TryUpdate(key, new ProgressEntry(write.Progress, DateTime.UtcNow.Add(ttl)), current))
            {
                EnforceCapacity();
                return ProgressCompareAndSetResult.Updated;
            }
        }
    }

    private void CleanupPeriodically()
    {
        var nowTick = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastCleanupTick);
        if (nowTick - last < (long)_cleanupInterval.TotalMilliseconds
            || Interlocked.CompareExchange(ref _lastCleanupTick, nowTick, last) != last)
        {
            return;
        }

        Sweep();
    }

    private void EnforceCapacity()
    {
        if (_entries.Count <= MaxEntries)
        {
            CleanupPeriodically();
            return;
        }

        Interlocked.Exchange(ref _lastCleanupTick, Environment.TickCount64);
        Sweep();
    }

    private void Sweep()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in _entries.Where(entry => entry.Value.ExpiresAt <= now))
        {
            RemoveSnapshot(entry);
        }

        var overflow = _entries.Count - MaxEntries;
        if (overflow > 0)
        {
            foreach (var entry in _entries.OrderBy(entry => entry.Value.ExpiresAt).Take(overflow))
            {
                RemoveSnapshot(entry);
            }
        }
    }

    private bool RemoveSnapshot(KeyValuePair<string, ProgressEntry> entry)
        => ((ICollection<KeyValuePair<string, ProgressEntry>>)_entries).Remove(entry);

    private void Store(string operationId, IOperationProgress progress, TimeSpan ttl)
    {
        _entries[$"{KeyPrefix}{operationId}"] = new ProgressEntry(progress, DateTime.UtcNow.Add(ttl));
        EnforceCapacity();
    }

    private void RemoveEntry(string key)
        => _entries.TryRemove(key, out _);

    private List<string> GetActiveOperationIds(OperationType? operationType)
    {
        CleanupPeriodically();
        var now = DateTime.UtcNow;
        return _entries
            .Where(entry => entry.Key.StartsWith(KeyPrefix, StringComparison.Ordinal) && entry.Value.ExpiresAt > now)
            .Where(entry => operationType == null || entry.Value.Progress.Type == operationType)
            .Select(entry => entry.Key[KeyPrefix.Length..])
            .ToList();
    }
}
