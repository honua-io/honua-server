// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Default process-local content-hash artifact cache. Stores bytes in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the sha256 hex
/// digest; TTL-backed entries are garbage-collected lazily on read and on every
/// write. Non-TTL entries are bounded by <see cref="MaxEntries"/> and
/// <see cref="MaxTotalBytes"/> to prevent unbounded memory growth in long-running
/// servers that process many distinct specs.
/// </summary>
internal sealed class InMemoryContentHashArtifactCache : IContentHashArtifactCache
{
    /// <summary>Maximum number of entries retained across all TTL states.</summary>
    internal const int MaxEntries = 2048;

    /// <summary>Maximum total byte payload retained (512 MiB).</summary>
    internal const long MaxTotalBytes = 512L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public InMemoryContentHashArtifactCache(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public Task<CachedArtifactRef?> TryGetAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(contentHash, out var entry))
        {
            return Task.FromResult<CachedArtifactRef?>(null);
        }

        if (IsExpired(entry))
        {
            _entries.TryRemove(contentHash, out _);
            return Task.FromResult<CachedArtifactRef?>(null);
        }

        return Task.FromResult<CachedArtifactRef?>(entry.Reference);
    }

    public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(contentHash);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_entries.TryGetValue(contentHash, out var entry))
        {
            return Task.FromResult<Stream?>(null);
        }

        if (IsExpired(entry))
        {
            _entries.TryRemove(contentHash, out _);
            return Task.FromResult<Stream?>(null);
        }

        Stream stream = new MemoryStream(entry.Bytes, writable: false);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        cancellationToken.ThrowIfCancellationRequested();

        // Cache keys are spec-derived; the executor declares the key via
        // payload.ContentHash and we trust it without re-hashing the body.
        var bytes = payload.Bytes.ToArray();
        var producedAt = _timeProvider.GetUtcNow();
        DateTimeOffset? expiresAt = payload.Ttl is TimeSpan ttl ? producedAt + ttl : null;

        var reference = new CachedArtifactRef
        {
            ContentHash = payload.ContentHash,
            Uri = $"/v1/spec/artifact/{payload.ContentHash}",
            Bytes = bytes.LongLength,
            ProducedAt = producedAt,
            ExpiresAt = expiresAt,
            ContentType = payload.ContentType
        };

        var entry = new CacheEntry(reference, bytes);
        _entries[payload.ContentHash] = entry;

        // Opportunistically reclaim TTL-expired entries on write. Without this
        // sweep, a workload that keeps producing unique mutable-source hashes
        // leaves expired byte arrays resident until the same hash is read again
        // — which may never happen, because the same mutable source is unlikely
        // to rehash to a previously-cached key. The sweep is O(n) in the number
        // of entries, but only inspects TTL-backed ones and uses a conditional
        // TryRemove, so it is safe against concurrent reads on the live entries.
        SweepExpired(producedAt);

        // Enforce hard bounds so non-TTL (deterministic compute) entries do not
        // accumulate indefinitely in long-running servers.  After the TTL sweep
        // above, evict arbitrary entries until we are under both the count and
        // byte-budget ceilings.  ConcurrentDictionary offers no ordering, so
        // eviction is not strict LRU; the goal is OOM prevention, not cache
        // optimality.
        EnforceBudget();

        return Task.FromResult(reference);
    }

    private bool IsExpired(CacheEntry entry)
    {
        return entry.Reference.ExpiresAt is DateTimeOffset expiry && expiry <= _timeProvider.GetUtcNow();
    }

    private void SweepExpired(DateTimeOffset asOf)
    {
        foreach (var kvp in _entries)
        {
            if (kvp.Value.Reference.ExpiresAt is DateTimeOffset expiry && expiry <= asOf)
            {
                // Conditional TryRemove: atomically removes only when the slot
                // still holds the exact CacheEntry we observed. Concurrent
                // writers that replaced the slot with a fresh entry keep it
                // untouched, because the record equality over a new byte[] and
                // fresh CachedArtifactRef will not match the captured one.
                _entries.TryRemove(kvp);
            }
        }
    }

    /// <summary>
    /// Evicts arbitrary entries until both <see cref="MaxEntries"/> and
    /// <see cref="MaxTotalBytes"/> constraints are satisfied.  Called after
    /// every <see cref="PutAsync"/> so the dictionary never grows without bound
    /// when the workload keeps producing distinct content hashes with no TTL.
    /// </summary>
    private void EnforceBudget()
    {
        // Fast-path: most calls are under budget.
        if (_entries.Count <= MaxEntries && TotalBytes() <= MaxTotalBytes)
        {
            return;
        }

        // Evict entries one by one until we are within budget.  We snapshot
        // the keys once so that the loop terminates even under concurrent writes.
        foreach (var kvp in _entries)
        {
            if (_entries.Count <= MaxEntries && TotalBytes() <= MaxTotalBytes)
            {
                break;
            }

            _entries.TryRemove(kvp);
        }
    }

    private long TotalBytes()
    {
        long total = 0;
        foreach (var kvp in _entries)
        {
            total += kvp.Value.Bytes.LongLength;
        }

        return total;
    }

    private sealed record CacheEntry(CachedArtifactRef Reference, byte[] Bytes);
}
