// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Provides access to the change-tracking log for incremental replication sync.
/// Reads from the feature_changes table populated by the database trigger.
/// </summary>
public interface IChangeTracker
{
    /// <summary>
    /// Gets the current value of the monotonic sync generation sequence
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current generation number, or 0 if the sequence has not been used</returns>
    Task<long> GetCurrentGenerationAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets collapsed changes since a given generation for the specified layers.
    /// Multiple changes to the same feature are collapsed into a single net operation:
    /// insert + update(s) → insert, insert + delete → omitted, update(s) + delete → delete.
    /// </summary>
    /// <param name="sinceGeneration">Generation number to start from (exclusive)</param>
    /// <param name="layerIds">Layer IDs to filter changes for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collapsed change feed ordered by generation</returns>
    Task<IReadOnlyList<FeatureChange>> GetChangesSinceAsync(
        long sinceGeneration,
        int[] layerIds,
        CancellationToken cancellationToken = default);
}
