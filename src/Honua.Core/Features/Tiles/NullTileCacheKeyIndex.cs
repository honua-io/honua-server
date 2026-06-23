// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// No-op <see cref="ITileCacheKeyIndex" /> registered when tile-cache eviction is disabled or no
/// Redis backing store is configured (#1917). Keeps the hot tile-serve path unchanged: every method
/// is a cheap no-op and <see cref="IsEnabled" /> is <see langword="false" /> so callers can skip
/// building access records entirely.
/// </summary>
public sealed class NullTileCacheKeyIndex : ITileCacheKeyIndex
{
    /// <summary>
    /// A shared singleton instance, since the no-op holds no state.
    /// </summary>
    public static readonly NullTileCacheKeyIndex Instance = new();

    /// <inheritdoc />
    public bool IsEnabled => false;

    /// <inheritdoc />
    public Task RecordAccessAsync(string key, long sizeBytes, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TileCacheEntry>>([]);

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
