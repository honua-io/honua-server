// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Tiles;

/// <summary>
/// Abstraction for persisting and retrieving <see cref="TileCacheGenerationCheckpoint"/>
/// snapshots so bounded generated tile-cache seed/warm generations are resumable after a
/// failure or cancellation (issue #2661). A generation is keyed by its stable generation id
/// so a fix-forward retry resumes from the last completed metatile block and regenerates only
/// the failed or not-yet-attempted units.
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe to call concurrently for distinct generation identifiers.
/// Operations on a single generation identifier are serialized by the implementation.
/// </para>
/// <para>
/// Implementations must truncate the persisted failed-unit set to the deterministic upper bound
/// enforced by <see cref="TileCacheGenerationCheckpointBounds"/> so persisted state stays
/// release-safe regardless of gridset size.
/// </para>
/// </remarks>
public interface ITileCacheGenerationCheckpointStore
{
    /// <summary>
    /// Persist a checkpoint for the given generation, overwriting any previous checkpoint.
    /// </summary>
    /// <param name="checkpoint">Checkpoint to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SaveAsync(TileCacheGenerationCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the latest checkpoint for the given generation, or <c>null</c> when none exists.
    /// </summary>
    /// <param name="generationId">Stable generation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<TileCacheGenerationCheckpoint?> LoadAsync(string generationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the checkpoint for the given generation. Returns <c>true</c> when a checkpoint was
    /// removed; returns <c>false</c> when none existed.
    /// </summary>
    /// <param name="generationId">Stable generation identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> DeleteAsync(string generationId, CancellationToken cancellationToken = default);
}
