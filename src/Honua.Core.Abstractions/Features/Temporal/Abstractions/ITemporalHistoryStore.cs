// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Temporal.Domain;

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Provider-backed read primitive for the uncollapsed change log with attribution (slices 2-4 of
/// honua-server#1166). The slice-1 <c>IChangeTracker</c> exposes only the collapsed replication feed; the
/// diff and timeline surfaces need every individual revision plus the attribution recorded alongside it,
/// so this store reads the raw <c>honua.feature_changes</c> rows directly.
/// </summary>
/// <remarks>
/// Read-only providers register a no-op implementation so DI activation succeeds; those layers report no
/// history. Implementations must not leak provider internals (SQL, connection strings) through exceptions.
/// </remarks>
public interface ITemporalHistoryStore
{
    /// <summary>
    /// Reads every uncollapsed change-log row for a single feature, ordered by generation ascending.
    /// </summary>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="objectId">Stable object id whose revisions are requested.</param>
    /// <param name="afterGeneration">Exclusive lower bound on generation for pagination (0 for the first page).</param>
    /// <param name="limit">Maximum number of revisions to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Ordered revisions for the feature (oldest first).</returns>
    Task<IReadOnlyList<TemporalChangeRecord>> GetFeatureRevisionsAsync(
        int storageLayerId,
        long objectId,
        long afterGeneration,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the uncollapsed change-log rows for a layer within an exclusive generation window
    /// <c>(fromGeneration, toGeneration]</c>, ordered by object id then generation ascending.
    /// </summary>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="fromGeneration">Exclusive lower bound generation (the base checkpoint).</param>
    /// <param name="toGeneration">Inclusive upper bound generation (the target checkpoint).</param>
    /// <param name="afterObjectId">Exclusive lower bound on object id for pagination (0 for the first page).</param>
    /// <param name="limit">Maximum number of distinct features to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Change-log rows within the window, ordered by object id then generation.</returns>
    Task<IReadOnlyList<TemporalChangeRecord>> GetChangesInWindowAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        long afterObjectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the distinct features changed for a layer within the exclusive generation window
    /// <c>(fromGeneration, toGeneration]</c>. Used to produce diff summary totals and rollback affected
    /// counts without materializing every row.
    /// </summary>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="fromGeneration">Exclusive lower bound generation.</param>
    /// <param name="toGeneration">Inclusive upper bound generation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of distinct features changed within the window.</returns>
    Task<int> CountChangedFeaturesAsync(
        int storageLayerId,
        long fromGeneration,
        long toGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a non-generation checkpoint kind to a concrete change-tracker generation by querying the
    /// change log's recorded dimensions (timestamp, version/release, attribution source id, named
    /// checkpoint). Returns the highest generation that satisfies the checkpoint, or null when the
    /// checkpoint does not resolve for the layer.
    /// </summary>
    /// <param name="storageLayerId">Storage-layer id resolved from the metadata graph.</param>
    /// <param name="kind">The checkpoint kind to resolve (never <see cref="TemporalCheckpointKind.Generation"/>).</param>
    /// <param name="value">The raw checkpoint value supplied by the client.</param>
    /// <param name="upperBoundGeneration">The current generation; the resolved generation never exceeds this.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved generation, or null when the checkpoint does not resolve.</returns>
    Task<long?> ResolveCheckpointGenerationAsync(
        int storageLayerId,
        TemporalCheckpointKind kind,
        string value,
        long upperBoundGeneration,
        CancellationToken cancellationToken = default);
}
