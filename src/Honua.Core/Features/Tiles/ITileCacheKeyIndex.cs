// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Honua.Core.Features.Tiles;

// An unavailable snapshot must not be treated as an empty cache by a destructive lifecycle operation.
public readonly record struct TileCacheIndexSnapshot(
    IReadOnlyList<TileCacheEntry> Entries,
    bool IsAvailable);

public readonly record struct TileCacheIndexPage(
    IReadOnlyList<TileCacheEntry> Entries,
    bool IsAvailable,
    bool HasMore);

public interface ITileCacheKeyIndex
{
    bool IsEnabled { get; }

    // Access records are best-effort bookkeeping and must not fail the tile request. A write-record failure must fail the cache write because it advances lifecycle fencing.
    Task RecordAccessAsync(
        string key,
        long sizeBytes,
        DateTimeOffset? expiresAt,
        string? tenantScope = null,
        CancellationToken cancellationToken = default);

    Task RecordAccessIfCurrentAsync(
        string key,
        long sizeBytes,
        DateTimeOffset? expiresAt,
        string? tenantScope,
        string? observedWriteVersion,
        CancellationToken cancellationToken = default)
        => RecordAccessAsync(key, sizeBytes, expiresAt, tenantScope, cancellationToken);

    Task RecordWriteAsync(
        string key,
        long sizeBytes,
        DateTimeOffset expiresAt,
        string? tenantScope = null,
        CancellationToken cancellationToken = default);

    Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> MarkExpiredAsync(string key, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default);

    async Task<TileCacheIndexSnapshot> SnapshotWithStatusAsync(
        CancellationToken cancellationToken = default)
        => new(
            await SnapshotAsync(cancellationToken).ConfigureAwait(false),
            IsAvailable: true);

    // A distributed backend must page the index so a large cache is not one blocking command or one unbounded payload.
    IAsyncEnumerable<TileCacheIndexPage> ReadPagesAsync(
        int pageSize,
        CancellationToken cancellationToken = default)
        => ReadSinglePageAsync(this, pageSize, cancellationToken);

    private static async IAsyncEnumerable<TileCacheIndexPage> ReadSinglePageAsync(
        ITileCacheKeyIndex index,
        int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var snapshot = await index.SnapshotWithStatusAsync(cancellationToken).ConfigureAwait(false);
        var entries = snapshot.Entries
            .OrderBy(static entry => entry.LastAccessUtc)
            .ThenBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        yield return new TileCacheIndexPage(entries, snapshot.IsAvailable, HasMore: false);
    }

    Task RemoveAsync(string key, CancellationToken cancellationToken = default);

    // Remove the snapshotted entry only when no later successful write replaced it, so a lifecycle delete cannot erase bookkeeping for an object regenerated before the index mutation.
    async Task<bool> TryRemoveAsync(
        TileCacheEntry entry,
        CancellationToken cancellationToken = default)
    {
        await RemoveAsync(entry.Key, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
