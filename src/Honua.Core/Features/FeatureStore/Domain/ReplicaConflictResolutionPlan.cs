// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// The feature-store effect an operator-selected conflict resolution must produce to make the chosen
/// resolution real. Conflict review is not bookkeeping: every resolution that claims a new committed
/// server state has to write that state through the shared edit pipeline (#2430).
/// </summary>
public enum ReplicaConflictResolutionEffect
{
    /// <summary>
    /// No write is required: the committed server state already matches the operator's choice (for
    /// example accepting a client edit that last-write-wins already applied, or keeping the server
    /// state after a manual-review sync that never applied the client edit).
    /// </summary>
    None = 0,

    /// <summary>
    /// The resolved feature state must be written over the current server row through the shared edit
    /// pipeline.
    /// </summary>
    WriteFeatureState = 1,

    /// <summary>The feature must be deleted through the shared edit pipeline.</summary>
    DeleteFeature = 2,
}

/// <summary>
/// Why a requested conflict resolution cannot be planned. Distinguishes a malformed operator request
/// from a well-formed request that does not apply to the conflict's recorded state, so the admin
/// surface can map them to different HTTP statuses.
/// </summary>
public enum ReplicaConflictResolutionRejection
{
    /// <summary>The resolution was planned successfully.</summary>
    None = 0,

    /// <summary>The request payload was missing or malformed for the selected action (400).</summary>
    InvalidRequest = 1,

    /// <summary>
    /// The action does not apply to this conflict's type or recorded state (409), for example
    /// re-applying a client update to a feature the server has already deleted.
    /// </summary>
    NotApplicable = 2,
}

/// <summary>
/// Operator-supplied inputs for the resolution actions that need more than an action name:
/// <see cref="ReplicaConflictResolutionAction.MergeFields"/> needs the merged field values and
/// <see cref="ReplicaConflictResolutionAction.ChooseGeometry"/> needs the winning geometry side.
/// </summary>
/// <param name="FieldValues">
/// Operator-selected attribute values applied over the resolved feature state for a field merge.
/// </param>
/// <param name="GeometrySource">
/// Which captured state supplies the winning geometry: <c>client</c> or <c>server</c>.
/// </param>
public readonly record struct ReplicaConflictResolutionInputs(
    IReadOnlyDictionary<string, JsonElement>? FieldValues,
    string? GeometrySource);

/// <summary>
/// The planned outcome of an operator-selected conflict resolution: the feature-store effect to apply,
/// the resolved feature state to write (when any), and whether the resolution produces a new committed
/// server state. A rejected plan carries the reason and a client-safe message instead.
/// </summary>
/// <param name="Effect">Feature-store effect the resolution must produce.</param>
/// <param name="FeatureStateJson">
/// Resolved feature-state envelope (<c>{"attributes": {...}, "geometry": ...}</c>) to write when
/// <paramref name="Effect"/> is <see cref="ReplicaConflictResolutionEffect.WriteFeatureState"/>.
/// </param>
/// <param name="CommittedNewServerState">
/// Whether applying this plan produces a new committed server state (and therefore a new server
/// generation cursor on the conflict record).
/// </param>
/// <param name="Rejection">Rejection reason, or <see cref="ReplicaConflictResolutionRejection.None"/>.</param>
/// <param name="RejectionMessage">Client-safe rejection message when rejected.</param>
public readonly record struct ReplicaConflictResolutionPlan(
    ReplicaConflictResolutionEffect Effect,
    string? FeatureStateJson,
    bool CommittedNewServerState,
    ReplicaConflictResolutionRejection Rejection,
    string? RejectionMessage)
{
    /// <summary>True when the plan can be applied.</summary>
    public bool IsAccepted => Rejection == ReplicaConflictResolutionRejection.None;
}

/// <summary>
/// Command handed to <see cref="Abstractions.IReplicaConflictResolutionApplier"/> to make a planned
/// resolution real through the shared edit pipeline.
/// </summary>
/// <param name="ServiceId">Service the conflicting feature belongs to.</param>
/// <param name="PublicLayerId">Service-local layer id of the conflicting feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
/// <param name="Effect">Effect to apply (never <see cref="ReplicaConflictResolutionEffect.None"/>).</param>
/// <param name="FeatureStateJson">
/// Resolved feature-state envelope to write for
/// <see cref="ReplicaConflictResolutionEffect.WriteFeatureState"/>.
/// </param>
/// <param name="StorageLayerId">
/// Storage-layer id of the conflicting feature, when the conflict recorded one. Lets the applier read
/// the row it is about to overwrite so it can carry an optimistic-concurrency precondition into the
/// write transaction (#2430).
/// </param>
public readonly record struct ReplicaConflictResolutionCommand(
    string ServiceId,
    int PublicLayerId,
    long ObjectId,
    ReplicaConflictResolutionEffect Effect,
    string? FeatureStateJson,
    int? StorageLayerId = null);

/// <summary>
/// Outcome of applying a planned conflict resolution through the shared edit pipeline.
/// </summary>
/// <param name="Applied">True when the resolved state was committed.</param>
/// <param name="FailureMessage">
/// Sanitized failure message when the write did not commit. Never carries provider internals.
/// </param>
/// <param name="PreconditionFailed">
/// True when the write was rejected because the feature changed between the resolution's staleness
/// check and the write transaction. The resolution's own precondition caught a post-conflict edit that
/// arrived inside that window, so this is a stale resolution rather than a failure (#2430).
/// </param>
/// <param name="CommitOutcomeUnknown">
/// True when the pipeline could not determine whether the write committed — a lost commit
/// acknowledgement, say. Distinct from <paramref name="Applied"/> being false, which asserts the write
/// did NOT happen: an indeterminate outcome must keep the resolution claimed and resumable rather than
/// released, because releasing it lets the next attempt see this resolution's own change as a
/// post-conflict edit and strand the conflict as permanently stale (#2430).
/// </param>
public readonly record struct ReplicaConflictApplyResult(
    bool Applied,
    string? FailureMessage,
    bool CommitOutcomeUnknown = false,
    bool PreconditionFailed = false);
