// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Abstraction for persisting and retrieving <see cref="MigrationRunCheckpoint"/>
/// snapshots so migration runs are resumable after simulated source failures,
/// cancellation, and transient network errors (issue #1033 slice 3).
/// </summary>
/// <remarks>
/// <para>
/// Implementations must be safe to call concurrently for distinct run identifiers.
/// Operations on a single run identifier are serialized by the implementation.
/// </para>
/// <para>
/// Implementations must reject markers that look like URLs (contain <c>://</c>) and
/// must truncate marker payloads to a deterministic upper bound so persisted state
/// remains release-safe.
/// </para>
/// </remarks>
public interface IMigrationRunCheckpointStore
{
    /// <summary>
    /// Persist a checkpoint for the given run, overwriting any previous checkpoint.
    /// </summary>
    /// <param name="checkpoint">Checkpoint to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask SaveAsync(MigrationRunCheckpoint checkpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// Load the latest checkpoint for the given run, or <c>null</c> when no checkpoint
    /// exists.
    /// </summary>
    /// <param name="runId">Stable run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<MigrationRunCheckpoint?> LoadAsync(string runId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the checkpoint for the given run. Returns <c>true</c> when a checkpoint
    /// was removed; returns <c>false</c> when no checkpoint existed.
    /// </summary>
    /// <param name="runId">Stable run identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    ValueTask<bool> DeleteAsync(string runId, CancellationToken cancellationToken = default);
}
