// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;

namespace Honua.Core.Features.Tiles;

public sealed class InMemoryTileCacheGenerationCheckpointStore : ITileCacheGenerationCheckpointStore
{
    private readonly ConcurrentDictionary<string, TileCacheGenerationCheckpoint> _checkpoints =
        new(StringComparer.Ordinal);

    public int Count => _checkpoints.Count;

    public ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitized = TileCacheGenerationCheckpointBounds.Sanitize(checkpoint);
        _checkpoints[sanitized.GenerationId] = sanitized;
        return ValueTask.CompletedTask;
    }

    public ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        return _checkpoints.TryGetValue(generationId, out var checkpoint)
            ? ValueTask.FromResult<TileCacheGenerationCheckpoint?>(checkpoint)
            : ValueTask.FromResult<TileCacheGenerationCheckpoint?>(null);
    }

    public ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(generationId);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(_checkpoints.TryRemove(generationId, out _));
    }
}
