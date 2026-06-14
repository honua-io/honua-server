// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Operator-facing summary of a durable disconnected-sync conflict, returned by the conflict-list
/// endpoint so Console and operators can triage pending conflicts (#1167).
/// </summary>
public sealed class ReplicaConflictSummary
{
    /// <summary>Unique conflict identifier (GUID hex).</summary>
    public required string ConflictId { get; init; }

    /// <summary>Replica whose upload produced the conflict.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Service the replica belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer id of the conflicting feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Conflict classification: attribute, geometry, deleteUpdate, updateDelete, duplicateInsert, attachment, relationship.</summary>
    public required string ConflictType { get; init; }

    /// <summary>Lifecycle status: pending, resolved, deferred.</summary>
    public required string Status { get; init; }

    /// <summary>Server generation cursor captured when the conflict was recorded.</summary>
    public required long ServerGeneration { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was first recorded.</summary>
    public required DateTimeOffset DetectedAt { get; init; }
}

/// <summary>
/// Detailed conflict view returning base/client/server feature states and resolution evidence, so
/// the Console temporal-data-viewer can render the field- and geometry-level comparison (#1167).
/// </summary>
public sealed class ReplicaConflictDetail
{
    /// <summary>Unique conflict identifier (GUID hex).</summary>
    public required string ConflictId { get; init; }

    /// <summary>Replica whose upload produced the conflict.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Service the replica belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Service-local layer id of the conflicting feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Stable object id of the conflicting feature.</summary>
    public required long ObjectId { get; init; }

    /// <summary>Conflict classification.</summary>
    public required string ConflictType { get; init; }

    /// <summary>Lifecycle status: pending, resolved, deferred.</summary>
    public required string Status { get; init; }

    /// <summary>Correlation id of the synchronize call that produced the conflict, when known.</summary>
    public string? SyncOperationId { get; init; }

    /// <summary>Device/client that uploaded the conflicting edit, when supplied.</summary>
    public string? DeviceId { get; init; }

    /// <summary>User that owns the conflicting edit, when known.</summary>
    public string? UserId { get; init; }

    /// <summary>Server generation cursor captured when the conflict was recorded (temporal linkage).</summary>
    public required long ServerGeneration { get; init; }

    /// <summary>Base/common-ancestor feature state, when known. Opaque feature object.</summary>
    public System.Text.Json.JsonElement? BaseState { get; init; }

    /// <summary>Incoming client feature state. Opaque feature object.</summary>
    public System.Text.Json.JsonElement? ClientState { get; init; }

    /// <summary>Current server feature state. Opaque feature object.</summary>
    public System.Text.Json.JsonElement? ServerState { get; init; }

    /// <summary>
    /// Whether the geometry differs between the client and server states. Null when geometry change
    /// cannot be determined (e.g. one side carries no captured geometry).
    /// </summary>
    public bool? GeometryChanged { get; init; }

    /// <summary>
    /// Per-attribute comparison across the base/client/server states, limited to the fields that
    /// diverge. Empty when the client/server states are not both available to compare.
    /// </summary>
    public ReplicaConflictFieldChange[]? FieldChanges { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was first recorded.</summary>
    public required DateTimeOffset DetectedAt { get; init; }

    /// <summary>Resolution action applied, when the conflict has been resolved or deferred.</summary>
    public string? ResolutionAction { get; init; }

    /// <summary>Operator that resolved the conflict, when resolved.</summary>
    public string? ResolvedBy { get; init; }

    /// <summary>Timestamp (UTC) when the conflict was resolved, when resolved.</summary>
    public DateTimeOffset? ResolvedAt { get; init; }

    /// <summary>Server generation produced by the resolution, when a new committed server state was created.</summary>
    public long? ResolvedServerGeneration { get; init; }
}

/// <summary>
/// A single attribute's value across the base, client, and server states of a conflict, with flags
/// indicating where it diverged from the common ancestor. Powers the field-level comparison the
/// Console conflict reviewer renders (#1287).
/// </summary>
public sealed class ReplicaConflictFieldChange
{
    /// <summary>Attribute (field) name.</summary>
    public required string Field { get; init; }

    /// <summary>Value in the base/common-ancestor state, when a base state was captured.</summary>
    public System.Text.Json.JsonElement? BaseValue { get; init; }

    /// <summary>Value in the incoming client state.</summary>
    public System.Text.Json.JsonElement? ClientValue { get; init; }

    /// <summary>Value in the current server state.</summary>
    public System.Text.Json.JsonElement? ServerValue { get; init; }

    /// <summary>
    /// Whether the client value diverged. When a base is present this is client≠base; without a base
    /// it means client and server disagree.
    /// </summary>
    public required bool ChangedOnClient { get; init; }

    /// <summary>
    /// Whether the server value diverged. When a base is present this is server≠base; without a base
    /// it means client and server disagree.
    /// </summary>
    public required bool ChangedOnServer { get; init; }
}

/// <summary>
/// Envelope returned by the conflict-list endpoint.
/// </summary>
public sealed class ReplicaConflictListResponse
{
    /// <summary>Service the replica belongs to.</summary>
    public required string ServiceId { get; init; }

    /// <summary>Replica whose conflicts are listed.</summary>
    public required string ReplicaId { get; init; }

    /// <summary>Status filter applied to the listing, when one was supplied.</summary>
    public string? StatusFilter { get; init; }

    /// <summary>Conflict summaries. May be empty.</summary>
    public required ReplicaConflictSummary[] Conflicts { get; init; }
}

/// <summary>
/// Request body for the conflict-resolution endpoint.
/// </summary>
public sealed class ReplicaConflictResolutionRequest
{
    /// <summary>
    /// Resolution action to apply: acceptClient, keepServer, mergeFields, chooseGeometry,
    /// rejectClient, or defer.
    /// </summary>
    public string? Action { get; init; }
}

/// <summary>
/// Response returned after a conflict resolution is applied.
/// </summary>
public sealed class ReplicaConflictResolutionResponse
{
    /// <summary>The conflict after resolution.</summary>
    public required ReplicaConflictDetail Conflict { get; init; }

    /// <summary>
    /// Whether the resolution created a new committed server state (true for accept-client and
    /// merge/geometry merges; false for keep-server, reject-client, and defer).
    /// </summary>
    public required bool CommittedNewServerState { get; init; }
}
