// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Protocols.GeoServices.FeatureServer.Models;

/// <summary>
/// Stable, machine-readable per-feature edit error codes surfaced in the
/// <see cref="EditError.Code"/> field of an <c>applyEdits</c> HTTP-200 result envelope
/// (<see cref="ApplyEditsResponse"/>). The Esri wire shape only carries a numeric
/// <c>code</c> and free-form <c>description</c>, so clients that need to branch on the
/// <em>kind</em> of failure (e.g. retry on a conflict, surface a "deleted elsewhere"
/// message, prompt re-authentication) must key on a deterministic code rather than parsing
/// the description. These codes are the stable contract for that classification (#2251):
/// once published they do not change meaning, and every per-feature failure the
/// FeatureServer edit path emits maps to exactly one of them.
/// </summary>
/// <remarks>
/// The codes occupy the Esri-conventional <c>1000+</c> per-edit error range and remain
/// compatible with ArcGIS clients, which treat any non-zero <c>code</c> with a
/// <c>success:false</c> result as an edit failure. The values are intentionally distinct so
/// the conflict classes the ticket calls out — update-update, delete-delete, not-found, and
/// lock/locked — are individually addressable, with <see cref="GenericFailure"/> as the
/// catch-all fallback.
/// </remarks>
public static class GeoServicesEditErrorCodes
{
    /// <summary>
    /// Unclassified / fallback failure. Emitted when no more specific classification applies
    /// (e.g. an unexpected provider error during the write).
    /// </summary>
    public const int GenericFailure = 1000;

    /// <summary>
    /// The update/delete operation did not carry a usable object id: the OBJECTID was missing,
    /// non-numeric, or the configured object-id field could not be resolved. A request-shape
    /// error, not a conflict — resubmitting the same payload will fail identically.
    /// </summary>
    public const int InvalidObjectId = 1001;

    /// <summary>
    /// The targeted feature for an <c>update</c> does not exist (or is not visible to the
    /// caller under row-level security). The "not-found" class: the object id has no matching
    /// row to update.
    /// </summary>
    public const int NotFound = 1002;

    /// <summary>
    /// The targeted feature for a <c>delete</c> no longer exists — it was already removed,
    /// typically by another writer. The "delete-delete" class: a concurrent or repeated delete
    /// of the same object id.
    /// </summary>
    public const int DeleteNotFound = 1003;

    /// <summary>
    /// The <c>update</c> (or delete) was rejected because the stored row changed since the
    /// caller observed it — an optimistic-concurrency / version mismatch. The "update-update"
    /// class: two writers raced to modify the same feature. Surfaced when a writer reports an
    /// <see cref="EditOperationResult.IsPreconditionFailure"/> (HTTP 412 semantics). Clients
    /// may re-read and retry.
    /// </summary>
    public const int UpdateConflict = 1004;

    /// <summary>
    /// The feature is locked by another editor/session and cannot be modified right now. The
    /// "lock/locked" class. Reserved for lock-aware writers: surfaced when a writer reports a
    /// lock failure (HTTP 423 semantics). No writer in the default <c>applyEdits</c> path
    /// produces feature locks today, but the code is part of the stable contract so lock-aware
    /// providers map onto it without a client change.
    /// </summary>
    public const int FeatureLocked = 1005;

    /// <summary>
    /// The feature payload failed validation before the write: invalid attributes, invalid or
    /// mismatched geometry, an attribute-rule violation, or an invalid contingent-value
    /// combination. A request-shape error, not a conflict.
    /// </summary>
    public const int ValidationFailed = 1006;

    /// <summary>
    /// The edit was denied by an owner-based edit policy: a non-owning, non-admin principal
    /// (or an anonymous caller, while the policy is active) attempted to modify or delete a row
    /// owned by another principal. An authorization failure, not a conflict.
    /// </summary>
    public const int NotPermitted = 1007;

    /// <summary>
    /// The operation itself was otherwise applicable but the whole transaction was rolled back
    /// because a sibling operation in the same request failed and <c>rollbackOnFailure=true</c>.
    /// </summary>
    public const int OperationRolledBack = 1008;

    /// <summary>
    /// Maps a failed canonical <see cref="EditOperationResult"/> returned by a feature writer
    /// onto the stable per-feature code that should appear in the <c>applyEdits</c> envelope.
    /// The classification is deterministic and never inspects the free-form error message:
    /// it keys only on the result's typed flags and numeric code.
    /// </summary>
    /// <param name="result">The writer's per-operation result (assumed to be a failure).</param>
    /// <param name="kind">Which operation the result belongs to, so a not-found is reported as
    /// <see cref="NotFound"/> for updates and <see cref="DeleteNotFound"/> for deletes.</param>
    /// <returns>The stable per-feature error code.</returns>
    public static int ClassifyWriterFailure(EditOperationResult result, FeatureEditOperationKind kind)
    {
        // Optimistic-concurrency conflict (update-update). The writer flags this explicitly via
        // EditOperationResult.PreconditionFailed (ErrorCode == 412); honour both the typed flag
        // and the numeric code so it classifies regardless of how the result was constructed.
        if (result.IsPreconditionFailure || result.ErrorCode == EditOperationResult.PreconditionFailedErrorCode)
        {
            return UpdateConflict;
        }

        return result.ErrorCode switch
        {
            // HTTP 404 — the row was gone by the time the write executed (a race that slipped
            // past the handler's pre-read). Reported per operation kind.
            404 => kind == FeatureEditOperationKind.Delete ? DeleteNotFound : NotFound,
            // HTTP 423 — a lock-aware writer reported the feature as locked.
            423 => FeatureLocked,
            _ => GenericFailure,
        };
    }
}
