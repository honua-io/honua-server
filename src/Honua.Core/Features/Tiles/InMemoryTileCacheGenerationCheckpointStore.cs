// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Core.Features.Tiles;

/// <summary>
/// In-memory implementation of <see cref="ITileCacheGenerationCheckpointStore"/> (issue #2661).
/// It is the default binding so stores-less dev/test profiles still resolve the dependency, and
/// backs short-lived in-process seed/warm generations that do not need to survive a process
/// restart. Redis-backed profiles override this with a durable, cross-node store. Every write is
/// bounded via <see cref="TileCacheGenerationCheckpointBounds.Sanitize"/>.
/// </summary>
public sealed class InMemoryTileCacheGenerationCheckpointStore : ITileCacheGenerationCheckpointStore
{
    private readonly ConcurrentDictionary<string, TileCacheGenerationCheckpoint> _checkpoints =
        new(StringComparer.Ordinal);

    /// <summary>Number of checkpoints currently persisted. Exposed for tests.</summary>
    public int Count => _checkpoints.Count;

    /// <inheritdoc />
    public ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = TileCacheGenerationCheckpointBounds.Sanitize(checkpoint);
        _checkpoints[sanitized.GenerationId] = sanitized;
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        return _checkpoints.TryGetValue(generationId, out var checkpoint)
            ? ValueTask.FromResult<TileCacheGenerationCheckpoint?>(checkpoint)
            : ValueTask.FromResult<TileCacheGenerationCheckpoint?>(null);
    }

    /// <inheritdoc />
    public ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_checkpoints.TryRemove(generationId, out _));
    }
}
