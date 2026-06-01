// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Request to resolve a pending disconnected-sync conflict (#1167).
/// </summary>
/// <param name="ConflictId">Conflict to resolve.</param>
/// <param name="Action">Resolution action selected by the operator.</param>
/// <param name="ResolvedBy">Identifier of the resolving operator (audit evidence).</param>
/// <param name="ResolvedAt">UTC timestamp of the resolution.</param>
/// <param name="ResolvedServerGeneration">
/// Server generation cursor produced by the resolution when a new committed server state was
/// created, or null when the resolution produced no new server state.
/// </param>
public readonly record struct ReplicaConflictResolution(
    string ConflictId,
    ReplicaConflictResolutionAction Action,
    string ResolvedBy,
    DateTimeOffset ResolvedAt,
    long? ResolvedServerGeneration);

/// <summary>
/// Outcome of a <see cref="IReplicaConflictRepository.ResolveAsync"/> call.
/// </summary>
/// <param name="Record">
/// The conflict record after the call, or null when the conflict does not exist.
/// </param>
/// <param name="Applied">
/// True when this call applied the resolution; false when the conflict was already resolved by a
/// prior (possibly concurrent) call, in which case <see cref="Record"/> reflects that prior
/// resolution and the caller must not re-report this request as a successful resolution.
/// </param>
public readonly record struct ReplicaConflictResolutionOutcome(
    ReplicaConflictRecord? Record,
    bool Applied);

/// <summary>
/// Persistent storage for durable disconnected-sync conflict records (#1167). Conflict records are
/// written when a replica upload cannot be applied cleanly and are reviewed/resolved through the
/// operator-facing admin API after the synchronize response has returned.
/// </summary>
/// <remarks>
/// Providers that cannot support manual conflict review (for example read-only analytics providers)
/// report <see cref="SupportsConflictReview"/> as <c>false</c>; the conflict-review endpoints then
/// return a not-supported denial rather than an empty result, so the unsupported case is explicit.
/// </remarks>
public interface IReplicaConflictRepository
{
    /// <summary>
    /// Whether this provider supports durable conflict review and resolution. Read-only providers
    /// return <c>false</c>.
    /// </summary>
    bool SupportsConflictReview { get; }

    /// <summary>
    /// Creates or updates a durable conflict record.
    /// </summary>
    Task UpsertAsync(ReplicaConflictRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists conflict records for a replica, optionally filtered to a single lifecycle status,
    /// ordered most-recent-first.
    /// </summary>
    Task<IReadOnlyList<ReplicaConflictRecord>> ListByReplicaAsync(
        string replicaId,
        ReplicaConflictStatus? status = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a single conflict record by id, or null when not found.
    /// </summary>
    Task<ReplicaConflictRecord?> GetAsync(string conflictId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a resolution to a pending or deferred conflict, transitioning it to a terminal status
    /// and recording the resolution evidence. The transition is atomic: if the conflict was already
    /// resolved (including by a concurrent caller) the resolution is not re-applied and the returned
    /// outcome reports <see cref="ReplicaConflictResolutionOutcome.Applied"/> as <c>false</c> with the
    /// prior record, so the losing caller of a race does not report a spurious success. A missing
    /// conflict yields a null record.
    /// </summary>
    Task<ReplicaConflictResolutionOutcome> ResolveAsync(
        ReplicaConflictResolution resolution,
        CancellationToken cancellationToken = default);
}
