// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Commits an operator-selected disconnected-sync conflict resolution to the feature store through the
/// shared edit pipeline. The concrete implementation is supplied by the protocol adapter that owns the
/// replica surface so a resolution reuses the same validation, authorization, geometry conversion,
/// telemetry, and transactional outbox behavior as any other edit, rather than the conflict-review
/// surface issuing its own data access (#2430).
/// </summary>
/// <remarks>
/// <para>
/// Conflict review was previously bookkeeping-only: resolving a conflict recorded an action and a
/// server-generation cursor but never wrote feature data, so "keep server" left the client's
/// last-write-wins overwrite in place and "merge fields"/"choose geometry" had no way to express a
/// merged result at all. This seam is what makes the recorded resolution match the committed state.
/// </para>
/// <para>
/// The applier is optional in the container: hosts without a replica-capable protocol adapter do not
/// register one, and the conflict-review surface then reports resolutions that need a write as
/// not-supported rather than silently recording a resolution it cannot honor.
/// </para>
/// </remarks>
public interface IReplicaConflictResolutionApplier
{
    /// <summary>
    /// Applies a planned resolution to the conflicting feature through the shared edit pipeline.
    /// </summary>
    /// <param name="command">The planned resolution effect and resolved feature state.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether the resolved state committed, with a sanitized failure message when it did not.</returns>
    Task<ReplicaConflictApplyResult> ApplyAsync(
        ReplicaConflictResolutionCommand command,
        CancellationToken cancellationToken = default);
}
