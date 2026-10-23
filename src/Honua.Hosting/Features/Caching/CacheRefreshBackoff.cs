// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Caching;

/// <summary>
/// Retains failed refresh keys only for their retry window and the next idle cleanup tick.
/// </summary>
internal sealed class CacheRefreshBackoff(TimeProvider timeProvider) : IDisposable
{
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(1);
    private readonly Lock _sync = new();
    private ITimer? _cleanupTimer;
    private bool _disposed;
    private readonly Dictionary<string, long> _retryAfterUtcTicks = new(StringComparer.Ordinal);

    internal int Count
    {
        get
        {
            lock (_sync)
            {
                return _retryAfterUtcTicks.Count;
            }
        }
    }

    internal void Set(string key)
    {
        lock (_sync)
        {
            // A callback already in flight may finish after the worker has stopped.
            if (!_disposed)
            {
                _retryAfterUtcTicks[key] = timeProvider.GetUtcNow().Add(FailureBackoff).UtcTicks;
            }
        }
    }

    internal void Remove(string key)
    {
        lock (_sync)
        {
            _retryAfterUtcTicks.Remove(key);
        }
    }

    internal bool IsActive(string key)
    {
        lock (_sync)
        {
            if (!_retryAfterUtcTicks.TryGetValue(key, out var retryAfterTicks))
            {
                return false;
            }

            if (retryAfterTicks > timeProvider.GetUtcNow().UtcTicks)
            {
                return true;
            }

            _retryAfterUtcTicks.Remove(key);
            return false;
        }
    }

    /// <summary>
    /// Starts cleanup independently of queue traffic. The worker owns the returned lifetime.
    /// </summary>
    internal CacheRefreshBackoff StartCleanup()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _cleanupTimer ??= timeProvider.CreateTimer(
                static state => ((CacheRefreshBackoff)state!).RemoveExpiredEntries(),
                this, FailureBackoff, FailureBackoff);
            return this;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ITimer? timer;
        lock (_sync)
        {
            _disposed = true;
            _retryAfterUtcTicks.Clear();
            timer = _cleanupTimer;
            _cleanupTimer = null;
        }

        timer?.Dispose();
    }

    internal void RemoveExpiredEntries()
    {
        lock (_sync)
        {
            var nowTicks = timeProvider.GetUtcNow().UtcTicks;
            // codeql[cs/linq/missed-where]: removes expired deadlines while enumerating the same dictionary
            foreach (var entry in _retryAfterUtcTicks)
            {
                if (entry.Value <= nowTicks)
                {
                    // Renewal and removal share the lock: cleanup cannot remove a newer deadline.
                    _retryAfterUtcTicks.Remove(entry.Key);
                }
            }
        }
    }
}
