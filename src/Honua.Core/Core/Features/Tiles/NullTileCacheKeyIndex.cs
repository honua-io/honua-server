// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Honua.Core.Features.Tiles;

public sealed class NullTileCacheKeyIndex : ITileCacheKeyIndex
{
    public static readonly NullTileCacheKeyIndex Instance = new();

    public bool IsEnabled => false;

    public Task RecordAccessAsync(
        string key,
        long sizeBytes,
        DateTimeOffset? expiresAt,
        string? tenantScope = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RecordWriteAsync(
        string key,
        long sizeBytes,
        DateTimeOffset expiresAt,
        string? tenantScope = null,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<bool> IsExpiredAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<bool> MarkExpiredAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<IReadOnlyList<TileCacheEntry>> SnapshotAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<TileCacheEntry>>([]);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
