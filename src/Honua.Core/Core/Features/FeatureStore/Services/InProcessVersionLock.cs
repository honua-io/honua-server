// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed class InProcessVersionLock : IVersionLock
{
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.Ordinal);

    public Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var key = $"{service}:{versionId:N}";

        Gate gate;
        while (true)
        {
            gate = _gates.GetOrAdd(key, static _ => new Gate());
            if (gate.TryAddRef())
            {
                break;
            }

            // The gate was retired by the last releaser between our GetOrAdd and TryAddRef;
            // help remove the dead entry (no-op if already gone) and retry with a fresh gate.
            _gates.TryRemove(KeyValuePair.Create(key, gate));
        }

        // Non-blocking acquisition: a contended lock returns null so the caller can surface a clear
        // in-progress response rather than queueing behind the in-flight operation.
        bool acquired;
        try
        {
            acquired = gate.Semaphore.Wait(0, cancellationToken);
        }
        catch
        {
            ReleaseRef(key, gate);
            throw;
        }

        if (!acquired)
        {
            ReleaseRef(key, gate);
            return Task.FromResult<IVersionLockHandle?>(null);
        }

        return Task.FromResult<IVersionLockHandle?>(new Handle(this, key, gate));
    }

    private void ReleaseRef(string key, Gate gate)
    {
        if (gate.ReleaseRef())
        {
            // Last reference dropped and the gate is now retired: no holder, no waiter, and any
            // concurrent acquirer that fetched this instance will fail TryAddRef and retry.
            _gates.TryRemove(KeyValuePair.Create(key, gate));
            gate.Semaphore.Dispose();
        }
    }

    private sealed class Gate
    {
        private int _refCount;

        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public bool TryAddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                if (current < 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public bool ReleaseRef()
        {
            return Interlocked.Decrement(ref _refCount) == 0 &&
                Interlocked.CompareExchange(ref _refCount, -1, 0) == 0;
        }
    }

    private sealed class Handle(InProcessVersionLock owner, string key, Gate gate) : IVersionLockHandle
    {
        private int _released;

        public CancellationToken LockLostToken => CancellationToken.None;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Semaphore.Release();
                owner.ReleaseRef(key, gate);
            }

            return ValueTask.CompletedTask;
        }
    }
}
