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
/// <param name="ResolutionInputHash">
/// Hash of the resolution inputs this claim was taken with, so a later resume cannot finalize this
/// write while reporting a different requested state (#2430).
/// </param>
public readonly record struct ReplicaConflictResolution(
    string ConflictId,
    ReplicaConflictResolutionAction Action,
    string ResolvedBy,
    DateTimeOffset ResolvedAt,
    long? ResolvedServerGeneration,
    string? ResolutionInputHash = null);

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
/// A detection-time correction to an already-recorded conflict: the refined classification, the
/// captured client/server state envelopes, and whether the client edit committed. Applied only while
/// the conflict is still pending (#2430).
/// </summary>
/// <remarks>
/// Detection-time post-processing (state capture, classification refinement, and the client-edit
/// outcome) can still be running when an operator resolves the freshly listed conflict. A read-then-
/// write of the whole record would race that resolution and silently reopen it — writing back the
/// stale <c>Pending</c> status and clearing the resolution evidence. This update therefore touches
/// only the detection-owned columns and is guarded on the pending status, so a resolved conflict is
/// left alone.
/// </remarks>
/// <param name="ConflictId">Conflict to correct.</param>
/// <param name="ConflictType">Refined classification, or null to leave it unchanged.</param>
/// <param name="ClientStateJson">Captured client state, or null to leave it unchanged.</param>
/// <param name="ServerStateJson">Captured pre-apply server state, or null to leave it unchanged.</param>
/// <param name="ClientEditApplied">
/// Whether the conflicting client edit committed, or null to leave it unchanged.
/// </param>
/// <param name="ResolutionBaseGeneration">
/// Server generation as of the moment the conflict's own sync batch finished touching its layer — the
/// cursor the captured states describe, and the precondition a later resolution checks against. Null
/// leaves it unchanged.
/// </param>
/// <param name="ClientEditOutcomeUnknown">
/// Whether the storage layer could not say if the conflicting client edit committed, or null to leave
/// it unchanged.
/// </param>
/// <param name="ClientEditSuperseded">
/// Whether this edit committed but was superseded by a later edit in the same upload, or null to leave
/// it unchanged.
/// </param>
/// <param name="ClearServerState">
/// Explicitly removes the captured server envelope. Null means "leave unchanged" for every other field,
/// so a re-baseline onto a row that no longer exists needs this to stop the record from advertising a
/// server state the feature no longer has.
/// </param>
public readonly record struct ReplicaConflictDetectionUpdate(
    string ConflictId,
    ReplicaConflictType? ConflictType,
    string? ClientStateJson,
    string? ServerStateJson,
    bool? ClientEditApplied,
    long? ResolutionBaseGeneration = null,
    bool? ClientEditOutcomeUnknown = null,
    bool? ClientEditSuperseded = null,
    bool ClearServerState = false);

/// <summary>
/// A progress marker for an in-flight resolution: whether its feature write has committed, the
/// generation that write produced, and whether finalization is complete. Lets an interrupted
/// resolution be resumed exactly once rather than leaving the conflict terminally claimed with its
/// generation or audit evidence missing (#2430).
/// </summary>
/// <param name="ConflictId">Conflict being resolved.</param>
/// <param name="ResolvedBy">
/// Operator whose claim this progress belongs to. The update only applies when the row still carries
/// this claim, so a superseded resolution's late finalizer cannot stamp a newer one.
/// </param>
/// <param name="Action">
/// Action of the claim this progress belongs to, matched for the same reason as
/// <paramref name="ResolvedBy"/> — a deferral that is later superseded by a real resolution must not
/// finalize it.
/// </param>
/// <param name="ResolvedAt">
/// Timestamp of the claim this progress belongs to. Operator and action are not unique over time: an
/// expired claim can be released and re-taken by the same operator with the same action, and without
/// the timestamp a slow original attempt could mark that replacement complete before its own write and
/// audit finished (#2430).
/// </param>
/// <param name="WriteCommitted">
/// Whether the resolution's feature write has committed, or null to leave it unchanged.
/// </param>
/// <param name="ResolvedServerGeneration">
/// Generation produced by the committed write, or null to leave it unchanged.
/// </param>
/// <param name="PreWriteStateToken">
/// Optimistic-concurrency token for the row as it was when the resolution was claimed, or null to
/// leave it unchanged. A recovery re-applies the write against this token rather than one derived at
/// retry time.
/// </param>
/// <param name="Finalized">
/// Whether finalization is complete, or null to leave it unchanged.
/// </param>
public readonly record struct ReplicaConflictFinalizationUpdate(
    string ConflictId,
    string ResolvedBy,
    ReplicaConflictResolutionAction Action,
    DateTimeOffset ResolvedAt,
    bool? WriteCommitted,
    long? ResolvedServerGeneration,
    bool? Finalized,
    string? PreWriteStateToken = null);

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
    /// Applies a detection-time correction to a still-pending conflict, touching only the
    /// detection-owned columns. Returns true when a pending conflict was updated; false when the
    /// conflict is missing or has already been resolved or deferred, in which case the caller must
    /// leave it alone rather than rewriting it from a stale read (#2430).
    /// </summary>
    Task<bool> TryUpdateDetectionStateAsync(
        ReplicaConflictDetectionUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records resolution progress on a conflict that is no longer pending. Returns true when the row
    /// was updated. Used to mark the feature write committed before finalization begins, and to mark
    /// finalization complete once the produced generation and audit evidence are durable, so an
    /// interrupted resolution can be resumed without re-applying the write (#2430).
    /// </summary>
    Task<bool> TryUpdateFinalizationStateAsync(
        ReplicaConflictFinalizationUpdate update,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes over an expired claim, replacing its timestamp with <paramref name="newResolvedAt"/>, but
    /// only while the row still carries the claim identified by <paramref name="expectedResolvedAt"/>.
    /// Returns true for the single caller that wins.
    /// </summary>
    /// <remarks>
    /// Recovery of an abandoned claim re-dispatches the resolution write, so it must be single-winner
    /// in its own right: two retries that both judged the same expired claim abandoned would otherwise
    /// both re-apply, and a failure in one would release a claim the other had already committed
    /// against (#2430).
    /// </remarks>
    Task<bool> TryTakeOverClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset expectedResolvedAt,
        DateTimeOffset newResolvedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a claimed conflict to the pending, reviewable state, but only while the row still
    /// carries the exact claim identified by the arguments. Returns true when the claim was released.
    /// </summary>
    /// <remarks>
    /// Bound to the claim rather than performing a whole-record write: two retries can both decide an
    /// expired claim is abandoned, and once the first has released it a third request can claim it
    /// again. An unconditional release would then clear that replacement claim and let its feature
    /// write proceed with no ownership, enabling a concurrent write (#2430). Matching
    /// <paramref name="resolvedAt"/> as well as the operator and action makes a replacement claim —
    /// which necessarily carries a new timestamp — non-matching.
    /// </remarks>
    Task<bool> TryReleaseClaimAsync(
        string conflictId,
        string resolvedBy,
        ReplicaConflictResolutionAction action,
        DateTimeOffset resolvedAt,
        CancellationToken cancellationToken = default);

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
