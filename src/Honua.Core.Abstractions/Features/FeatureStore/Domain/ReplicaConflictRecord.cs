// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Classification of a disconnected-sync conflict. Mirrors the Esri offline/disconnected
/// editing conflict taxonomy but is exposed as a Honua product contract rather than an
/// ArcGIS-named enumeration (#1167).
/// </summary>
public enum ReplicaConflictType
{
    /// <summary>Client and server changed overlapping attribute values for the same feature.</summary>
    Attribute = 0,

    /// <summary>Client and server changed the geometry of the same feature.</summary>
    Geometry = 1,

    /// <summary>Client deleted a feature the server updated since the base checkpoint.</summary>
    DeleteUpdate = 2,

    /// <summary>Client updated a feature the server deleted since the base checkpoint.</summary>
    UpdateDelete = 3,

    /// <summary>Client inserted a feature whose stable key already exists on the server.</summary>
    DuplicateInsert = 4,

    /// <summary>Conflicting attachment add/replace/delete for the same feature.</summary>
    Attachment = 5,

    /// <summary>Conflicting related-record edit that cannot be applied independently.</summary>
    Relationship = 6,
}

/// <summary>
/// Lifecycle status of a durable conflict record. A conflict is <see cref="Pending"/> when first
/// recorded and transitions to a terminal status once an operator submits a resolution.
/// </summary>
public enum ReplicaConflictStatus
{
    /// <summary>Conflict has been recorded but not yet reviewed/resolved.</summary>
    Pending = 0,

    /// <summary>Conflict was resolved by applying an operator-selected resolution.</summary>
    Resolved = 1,

    /// <summary>Conflict review was explicitly deferred and may be revisited later.</summary>
    Deferred = 2,
}

/// <summary>
/// Resolution action an operator may apply to a pending conflict. The chosen action determines
/// whether a new committed server state is produced (#1167).
/// </summary>
public enum ReplicaConflictResolutionAction
{
    /// <summary>Apply the client's edit, overwriting the conflicting server state.</summary>
    AcceptClient = 0,

    /// <summary>Discard the client's edit and keep the current server state.</summary>
    KeepServer = 1,

    /// <summary>Apply an operator-supplied merge of client and server field values.</summary>
    MergeFields = 2,

    /// <summary>Apply an operator-selected geometry (client or server) for a geometry conflict.</summary>
    ChooseGeometry = 3,

    /// <summary>Reject the client edit and record the rejection as audit evidence.</summary>
    RejectClient = 4,

    /// <summary>Defer the decision; the conflict remains reviewable.</summary>
    Defer = 5,
}

/// <summary>
/// Durable record of a single disconnected-sync conflict produced when a replica upload could not
/// be applied cleanly against current server state. Persisted so the conflict can be reviewed and
/// resolved after the synchronize response has returned (#1167). Base/client/server feature states
/// are stored as opaque pre-serialized JSON so the conflict-review surface stays decoupled from the
/// physical feature schema and from any ArcGIS-specific payload shape.
/// </summary>
public readonly record struct ReplicaConflictRecord
{
    /// <summary>Unique conflict identifier (GUID hex).</summary>
    public required string ConflictId { get; init; }

    /// <summary>Replica the conflicting upload belonged to.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Service the replica belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer id of the conflicting feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Conflict classification.</summary>
    public required ReplicaConflictType ConflictType { get; init; }

    /// <summary>Current lifecycle status.</summary>
    public required ReplicaConflictStatus Status { get; init; }

    /// <summary>
    /// Optional sync-operation correlation id linking the conflict to the synchronize call that
    /// produced it. Null when the producing operation was not correlated.
    /// </summary>
    public string? SyncOperationId { get; init; }

    /// <summary>
    /// Identifier of the device/client that uploaded the conflicting edit, when supplied.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Identifier of the user that owns the conflicting edit, when known.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Server generation cursor captured when the conflict was recorded. Links the conflict to the
    /// temporal change-set history (#1166).
    /// </summary>
    public required long ServerGeneration { get; init; }

    /// <summary>Pre-serialized JSON for the base (common-ancestor) feature state, when known.</summary>
    public string? BaseStateJson { get; init; }

    /// <summary>Pre-serialized JSON for the incoming client feature state.</summary>
    public string? ClientStateJson { get; init; }

    /// <summary>Pre-serialized JSON for the current server feature state.</summary>
    public string? ServerStateJson { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was first recorded.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Resolution action applied, when the conflict has been resolved.</summary>
    public ReplicaConflictResolutionAction? ResolutionAction { get; init; }

    /// <summary>Identifier of the operator that resolved the conflict, when resolved.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was resolved, when resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>
    /// Server generation cursor produced by the resolution when a new committed server state was
    /// created. Null when the resolution did not produce a new server state (e.g. keep-server/defer).
    /// </summary>
    public long? ResolvedServerGeneration { get; init; }
}
