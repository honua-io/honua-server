// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;

namespace Honua.Core.Features.Spec.Services;

/// <summary>
/// Default process-local content-hash artifact cache. Stores bytes in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by the sha256 hex
/// digest; TTL-backed entries are garbage-collected lazily on read.
/// </summary>
internal sealed class InMemoryContentHashArtifactCache : IContentHashArtifactCache
{
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
        return Task.FromResult(reference);
    }

    private bool IsExpired(CacheEntry entry)
    {
        return entry.Reference.ExpiresAt is DateTimeOffset expiry && expiry <= _timeProvider.GetUtcNow();
    }

    private sealed record CacheEntry(CachedArtifactRef Reference, byte[] Bytes);
}
