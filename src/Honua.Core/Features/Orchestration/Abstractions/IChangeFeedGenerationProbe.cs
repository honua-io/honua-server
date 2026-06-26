// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Orchestration.Abstractions;

/// <summary>
/// Probes the feature change-feed for the highest generation observed across a set of watched
/// layers since a base generation. The change feed (<c>feature_changes</c> / the monotonic
/// <c>sync_generation</c> sequence) already powers replica sync; this probe reuses it as an
/// event/CDC trigger source for the workflow scheduler.
/// </summary>
/// <remarks>
/// The probe is intentionally a narrow read over the change tracker so the event-trigger
/// background service stays decoupled from the scoped feature-store services and remains
/// unit-testable with an in-memory fake.
/// </remarks>
public interface IChangeFeedGenerationProbe
{
    /// <summary>
    /// Returns the highest change generation observed for any of the <paramref name="watchedLayerIds"/>
    /// strictly after <paramref name="sinceGeneration"/>, or <c>null</c> when no watched layer has
    /// advanced past the base generation. The returned generation is the marker the scheduler
    /// fires on and persists as the durable change-feed cursor.
    /// </summary>
    /// <param name="sinceGeneration">Base generation (exclusive) to look for advances past.</param>
    /// <param name="watchedLayerIds">Layer ids the trigger watches. Must be non-empty.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<long?> GetLatestGenerationAsync(
        long sinceGeneration,
        IReadOnlyList<int> watchedLayerIds,
        CancellationToken cancellationToken = default);
}
