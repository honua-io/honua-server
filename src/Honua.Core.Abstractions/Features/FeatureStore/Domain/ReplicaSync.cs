// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Direction of a replica synchronization. Mirrors the Esri <c>syncDirection</c> parameter but is
/// exposed as a protocol-neutral canonical contract (#1272).
/// </summary>
public enum ReplicaSyncDirection
{
    /// <summary>Server-to-client only: server changes are returned, no client edits are applied.</summary>
    Download = 0,

    /// <summary>Client-to-server only: client edits are uploaded and applied, no download delta.</summary>
    Upload = 1,

    /// <summary>Bidirectional: client edits are uploaded and applied, then a download delta is returned.</summary>
    Bidirectional = 2,
}

/// <summary>
/// A single uploaded edit within a replica synchronization. Conveys only what the canonical conflict
/// detector needs — the operation kind and (for update/delete) the conflict-detection object id —
/// plus an opaque <see cref="Payload"/> the protocol adapter uses to recover the original wire
/// feature when applying the edit through the shared edit pipeline. Geometry/attribute validation and
/// persistence stay in that shared pipeline; this record never carries provider-specific feature
/// shape into the canonical service.
/// </summary>
/// <param name="Kind">Operation kind (create/update/delete).</param>
/// <param name="ObjectId">
/// Stable object id for update/delete operations; null for create. Used as the conflict-detection
/// key against the server change log.
/// </param>
/// <param name="Payload">
/// Opaque protocol payload the adapter's replica edit applier casts back to its wire feature type.
/// The canonical service treats it as an opaque token.
/// </param>
public readonly record struct ReplicaUploadEdit(
    FeatureEditOperationKind Kind,
    long? ObjectId,
    object? Payload);

/// <summary>
/// The set of uploaded edits targeting a single service-local layer within a replica
/// synchronization. The canonical sync service resolves conflicts and applies non-conflicting edits
/// per layer.
/// </summary>
/// <param name="PublicLayerId">Service-local layer id the edits target.</param>
/// <param name="StorageLayerId">Storage-layer id used for change-log conflict detection.</param>
/// <param name="Edits">Uploaded edits for this layer.</param>
public readonly record struct ReplicaUploadLayerEdits(
    int PublicLayerId,
    int StorageLayerId,
    ImmutableArray<ReplicaUploadEdit> Edits);

/// <summary>
/// Canonical request to synchronize a replica. The GeoServices (and any other replica-capable
/// protocol) adapter maps its wire parameters onto this request; the shared replica-sync service owns
/// base-generation conflict detection, conflict-record writes, and the upload cursor (#1272).
/// </summary>
/// <param name="ReplicaId">Replica being synchronized.</param>
/// <param name="ServiceId">Service the replica belongs to.</param>
/// <param name="Direction">Requested sync direction.</param>
/// <param name="BaseGeneration">
/// The replica's last-known server generation cursor. Server changes to a feature after this cursor
/// indicate a conflicting concurrent server edit.
/// </param>
/// <param name="LayerEdits">Per-layer uploaded edits (empty for download-only sync).</param>
/// <param name="LastWriteWins">
/// When true (the default for non-manual conflict strategies), conflicting edits are still applied
/// (last-write-wins) but a durable conflict record is written when supported. When false, conflicting
/// edits are skipped and only recorded.
/// </param>
/// <param name="SyncOperationId">Optional correlation id linking produced conflicts to this call.</param>
/// <param name="DeviceId">Optional uploading device identifier (conflict audit evidence).</param>
/// <param name="UserId">Optional uploading user identifier (conflict audit evidence).</param>
public readonly record struct ReplicaSyncRequest(
    string ReplicaId,
    string ServiceId,
    ReplicaSyncDirection Direction,
    long BaseGeneration,
    ImmutableArray<ReplicaUploadLayerEdits> LayerEdits,
    bool LastWriteWins = true,
    string? SyncOperationId = null,
    string? DeviceId = null,
    string? UserId = null);

/// <summary>
/// A conflict detected while applying an uploaded replica edit: the client edit and a server edit to
/// the same <c>(layerId, objectId)</c> both occurred since the replica's base generation.
/// </summary>
/// <param name="PublicLayerId">Service-local layer id of the conflicting feature.</param>
/// <param name="ObjectId">Stable object id of the conflicting feature.</param>
/// <param name="ConflictType">Conflict classification.</param>
/// <param name="ClientKind">The client operation kind that conflicted.</param>
/// <param name="ServerOperation">The server operation that mutated the feature since base.</param>
/// <param name="Applied">
/// True when the client edit was still applied (last-write-wins); false when it was skipped.
/// </param>
/// <param name="ConflictId">
/// Durable conflict-record id when a record was written; null when conflict review is unsupported.
/// </param>
public readonly record struct ReplicaSyncConflict(
    int PublicLayerId,
    long ObjectId,
    ReplicaConflictType ConflictType,
    FeatureEditOperationKind ClientKind,
    FeatureChangeOperation ServerOperation,
    bool Applied,
    string? ConflictId);

/// <summary>
/// Outcome of applying the uploaded edits for a single layer through the shared edit pipeline.
/// </summary>
/// <param name="PublicLayerId">Service-local layer id.</param>
/// <param name="AppliedAdds">Number of create operations applied.</param>
/// <param name="AppliedUpdates">Number of update operations applied.</param>
/// <param name="AppliedDeletes">Number of delete operations applied.</param>
/// <param name="Failed">True when any non-conflict edit failed to apply.</param>
/// <param name="FailureMessage">Sanitized failure message when <paramref name="Failed"/> is true.</param>
public readonly record struct ReplicaLayerApplyResult(
    int PublicLayerId,
    int AppliedAdds,
    int AppliedUpdates,
    int AppliedDeletes,
    bool Failed,
    string? FailureMessage);

/// <summary>
/// Report produced by a replica upload/synchronize. Summarizes the applied edits, any detected
/// conflicts, and the resulting server generation cursor that a subsequent download delta should use
/// as its lower bound so the replica does not receive its own just-applied edits back (#1272).
/// </summary>
/// <param name="Success">
/// True when all non-conflicting edits applied cleanly. Conflicts under last-write-wins do not by
/// themselves fail the sync.
/// </param>
/// <param name="AppliedAdds">Total create operations applied across all layers.</param>
/// <param name="AppliedUpdates">Total update operations applied across all layers.</param>
/// <param name="AppliedDeletes">Total delete operations applied across all layers.</param>
/// <param name="Conflicts">Detected conflicts.</param>
/// <param name="LayerResults">Per-layer apply results.</param>
/// <param name="ServerGeneration">Server generation after the upload applied.</param>
/// <param name="FailureMessage">Sanitized failure message when <paramref name="Success"/> is false.</param>
public readonly record struct ReplicaSyncUploadReport(
    bool Success,
    int AppliedAdds,
    int AppliedUpdates,
    int AppliedDeletes,
    ImmutableArray<ReplicaSyncConflict> Conflicts,
    ImmutableArray<ReplicaLayerApplyResult> LayerResults,
    long ServerGeneration,
    string? FailureMessage);
