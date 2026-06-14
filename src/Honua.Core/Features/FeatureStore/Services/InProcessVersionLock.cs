// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Single-node fallback <see cref="IVersionLock"/> (#1553) used when Redis is not configured. Serializes
/// reconcile/post per (service, version) within one process using a keyed semaphore. Correct for
/// single-node and development/test deployments; the Redis-backed implementation supersedes it across
/// replicas. The lease duration is accepted for API parity but is a no-op in-process (the lock is
/// released when the handle is disposed, and a crash tears down the whole process anyway).
/// </summary>
public sealed class InProcessVersionLock : IVersionLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);

        var key = $"{service}:{versionId:N}";
        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        // Non-blocking acquisition: a contended lock returns null so the caller can surface a clear
        // in-progress response rather than queueing behind the in-flight operation.
        if (!gate.Wait(0, cancellationToken))
        {
            return Task.FromResult<IVersionLockHandle?>(null);
        }

        return Task.FromResult<IVersionLockHandle?>(new Handle(gate));
    }

    private sealed class Handle(SemaphoreSlim gate) : IVersionLockHandle
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                gate.Release();
            }

            return ValueTask.CompletedTask;
        }
    }
}
