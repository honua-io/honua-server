// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Classification of a disconnected-sync conflict between a client edit and the
/// current server state. Persisted as the SMALLINT <c>conflict_type</c> column.
/// </summary>
public enum ReplicaConflictType : short
{
    /// <summary>Client updated attributes of a feature the server also changed.</summary>
    Attribute = 1,

    /// <summary>Client updated the geometry of a feature the server also changed.</summary>
    Geometry = 2,

    /// <summary>Client updated a feature the server deleted since the base generation.</summary>
    UpdateDelete = 3,

    /// <summary>Client deleted a feature the server updated since the base generation.</summary>
    DeleteUpdate = 4,

    /// <summary>Client and server both deleted the same feature.</summary>
    DeleteDelete = 5,

    /// <summary>Client inserted a feature whose object id already exists on the server.</summary>
    DuplicateInsert = 6,
}

/// <summary>
/// Resolution action applied to a pending <see cref="ReplicaConflict"/>. Persisted as the
/// SMALLINT <c>resolution</c> column; <see langword="null"/> means still pending.
/// </summary>
public enum ReplicaConflictResolution : short
{
    /// <summary>Apply the client edit, overwriting the server state.</summary>
    AcceptClient = 1,

    /// <summary>Keep the current server state and discard the client edit.</summary>
    KeepServer = 2,

    /// <summary>Apply an operator-supplied merged feature payload.</summary>
    MergeFields = 3,

    /// <summary>Reject the client edit without further action.</summary>
    RejectClient = 4,

    /// <summary>Defer the decision; the conflict remains reviewable.</summary>
    Deferred = 5,
}

/// <summary>
/// Durable record of a disconnected-sync conflict. Created when a synchronizeReplica
/// upload edits a feature the server changed since the replica's base generation, so the
/// conflict can be reviewed and resolved after the sync response (#1167).
/// </summary>
public readonly record struct ReplicaConflict
{
    /// <summary>Stable conflict identifier.</summary>
    public required Guid ConflictId { get; init; }

    /// <summary>Replica the conflicting edit was uploaded against.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Groups all conflicts produced by a single synchronizeReplica upload.</summary>
    public required Guid SyncOpId { get; init; }

    /// <summary>Feature service the conflicting feature belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Layer of the conflicting feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Object id of the conflicting feature.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Conflict classification.</summary>
    public required ReplicaConflictType ConflictType { get; init; }

    /// <summary>Replica LastSyncGeneration when the conflict was detected.</summary>
    public required long BaseGeneration { get; init; }

    /// <summary>Submitted client feature state, as raw JSON.</summary>
    public required string ClientPayloadJson { get; init; }

    /// <summary>Current server feature state at detection time, as raw JSON.</summary>
    public required string ServerPayloadJson { get; init; }

    /// <summary>
    /// Server state at <see cref="BaseGeneration"/> (common ancestor), as raw JSON.
    /// <see langword="null"/> in the first slice; reserved for #1166 temporal snapshots.
    /// </summary>
    public string? BasePayloadJson { get; init; }

    /// <summary>Applied resolution, or <see langword="null"/> while pending.</summary>
    public ReplicaConflictResolution? Resolution { get; init; }

    /// <summary>Principal that resolved the conflict, when resolved.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Timestamp the conflict was resolved, when resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Operator-supplied merged feature payload for <see cref="ReplicaConflictResolution.MergeFields"/>.</summary>
    public string? ResolutionPayloadJson { get; init; }

    /// <summary>Timestamp the conflict was created.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Timestamp the conflict was last updated.</summary>
    public required DateTimeOffset UpdatedAt { get; init; }
}
