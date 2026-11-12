// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Infrastructure.Caching;

internal sealed partial class InMemoryCacheService
{
    private readonly ConcurrentDictionary<string, KeyLockEntry> _keyLocks = new(StringComparer.Ordinal);

    // An entry leaves the dictionary only when its last holder or waiter leaves, so every
    // caller for one key waits on the same semaphore and the dictionary holds only keys in use.
    private async ValueTask<KeyLock> AcquireKeyLockAsync(string key, CancellationToken cancellationToken)
    {
        var entry = RetainKeyLock(key);
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseKeyLockReference(key, entry);
            throw;
        }

        return new KeyLock(this, key, entry);
    }

    private KeyLockEntry RetainKeyLock(string key)
    {
        while (true)
        {
            var entry = _keyLocks.GetOrAdd(key, static _ => new KeyLockEntry());
            lock (entry)
            {
                if (!entry.Retired)
                {
                    entry.References++;
                    return entry;
                }
            }
        }
    }

    private void ReleaseKeyLockReference(string key, KeyLockEntry entry)
    {
        lock (entry)
        {
            if (--entry.References > 0)
            {
                return;
            }

            entry.Retired = true;
            _keyLocks.TryRemove(new KeyValuePair<string, KeyLockEntry>(key, entry));
        }
    }

    // The semaphores never allocate a wait handle, so in-flight callers may still release
    // theirs after the service is disposed.
    private sealed class KeyLockEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int References { get; set; }

        public bool Retired { get; set; }
    }

    private sealed class KeyLock(InMemoryCacheService owner, string key, KeyLockEntry entry) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            entry.Semaphore.Release();
            owner.ReleaseKeyLockReference(key, entry);
        }
    }
}
