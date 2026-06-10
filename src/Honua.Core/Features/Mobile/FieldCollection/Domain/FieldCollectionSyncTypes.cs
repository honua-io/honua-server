// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

/// <summary>
/// FieldCollection change operation types. Wire values mirror the mobile
/// <c>ChangeOperation</c> enum so server payloads round-trip without translation.
/// </summary>
public enum FieldCollectionChangeOperation : short
{
    /// <summary>The change inserts a new feature.</summary>
    Insert = 1,

    /// <summary>The change updates an existing feature.</summary>
    Update = 2,

    /// <summary>The change deletes an existing feature.</summary>
    Delete = 3,
}

/// <summary>
/// Outcome of a single FieldCollection push operation.
/// </summary>
public enum FieldCollectionPushOutcome : short
{
    /// <summary>The change was applied successfully.</summary>
    Applied = 1,

    /// <summary>The change conflicted with the current server state.</summary>
    Conflict = 2,

    /// <summary>The change was rejected by validation or policy.</summary>
    Rejected = 3,
}

/// <summary>
/// Conflict classification returned to clients when a push cannot be applied.
/// Wire values mirror the mobile <c>ConflictType</c> enum.
/// </summary>
public enum FieldCollectionConflictType : short
{
    /// <summary>No conflict occurred.</summary>
    None = 0,

    /// <summary>Both the client and server updated the feature.</summary>
    UpdateUpdate = 1,

    /// <summary>The client updated a feature that the server deleted.</summary>
    UpdateDelete = 2,

    /// <summary>The client deleted a feature that the server updated.</summary>
    DeleteUpdate = 3,

    /// <summary>Both the client and server deleted the feature.</summary>
    DeleteDelete = 4,
}

/// <summary>
/// A single FieldCollection feature change replayed to mobile clients during pull.
/// </summary>
public sealed record FieldCollectionChange
{
    /// <summary>Gets the monotonic server generation this change belongs to.</summary>
    public required long Generation { get; init; }

    /// <summary>Gets the identifier of the feature affected by the change.</summary>
    public required string FeatureId { get; init; }

    /// <summary>Gets the identifier of the layer that contains the feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Gets the operation applied to the feature.</summary>
    public required FieldCollectionChangeOperation Operation { get; init; }

    /// <summary>Gets the server-assigned version of the feature after the change.</summary>
    public required long Version { get; init; }

    /// <summary>Gets the timestamp at which the change was recorded.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Pre-serialized JSON payload of the feature at this version. Null for delete operations.
    /// </summary>
    public string? FeaturePayloadJson { get; init; }
}

/// <summary>
/// Page of FieldCollection changes returned by a pull request.
/// </summary>
public sealed record FieldCollectionChangesPage
{
    /// <summary>Gets the ordered changes contained in this page.</summary>
    public required IReadOnlyList<FieldCollectionChange> Changes { get; init; }

    /// <summary>Gets the current server generation at the time the page was produced.</summary>
    public required long ServerGeneration { get; init; }

    /// <summary>Gets the cursor to use when requesting the next page.</summary>
    public required long NextCursor { get; init; }

    /// <summary>Gets a value indicating whether more changes remain after this page.</summary>
    public required bool HasMore { get; init; }
}

/// <summary>
/// Inbound request describing a single mobile-pushed FieldCollection change.
/// </summary>
public sealed record FieldCollectionPushRequest
{
    /// <summary>Gets the client-assigned identifier that idempotently identifies this change.</summary>
    public required string ChangeId { get; init; }

    /// <summary>Gets the identifier of the feature affected by the change.</summary>
    public required string FeatureId { get; init; }

    /// <summary>Gets the identifier of the layer that contains the feature.</summary>
    public required int LayerId { get; init; }

    /// <summary>Gets the operation the client wants to apply to the feature.</summary>
    public required FieldCollectionChangeOperation Operation { get; init; }

    /// <summary>
    /// Server version that the client believes is the parent of this change.
    /// Required for update and delete operations to detect conflicts.
    /// Null is permitted only for inserts.
    /// </summary>
    public long? BaseVersion { get; init; }

    /// <summary>Gets the optional client timestamp at which the change was made.</summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>
    /// Pre-serialized JSON payload of the feature. Required for insert and update; null for delete.
    /// </summary>
    public string? FeaturePayloadJson { get; init; }
}

/// <summary>
/// Outcome envelope returned by a single push.
/// </summary>
public sealed record FieldCollectionPushResult
{
    /// <summary>Gets the client-assigned identifier of the change this result corresponds to.</summary>
    public required string ChangeId { get; init; }

    /// <summary>Gets the outcome of applying the change.</summary>
    public required FieldCollectionPushOutcome Outcome { get; init; }

    /// <summary>Gets the server generation after the push was processed.</summary>
    public required long ServerGeneration { get; init; }

    /// <summary>
    /// Server-assigned version after applying the change. Null when the push was not applied.
    /// </summary>
    public long? Version { get; init; }

    /// <summary>Gets the conflict classification when the push could not be applied.</summary>
    public FieldCollectionConflictType ConflictType { get; init; }

    /// <summary>
    /// Pre-serialized JSON payload of the current server feature. Populated on conflict
    /// when a server-side feature exists; null otherwise.
    /// </summary>
    public string? ServerFeaturePayloadJson { get; init; }

    /// <summary>
    /// Server version of the current server feature when a conflict is reported. Null otherwise.
    /// </summary>
    public long? ServerVersion { get; init; }

    /// <summary>
    /// Optional human-readable detail intended for diagnostics. Never includes
    /// SQL, stack traces, file paths, or connection strings.
    /// </summary>
    public string? RejectionReason { get; init; }
}

/// <summary>
/// Per-client cursor entry returned by the sync-cursor endpoint.
/// </summary>
public sealed record FieldCollectionSyncCursor
{
    /// <summary>Gets the identifier of the client this cursor belongs to.</summary>
    public required string ClientId { get; init; }

    /// <summary>Gets the last server generation the client has synchronized.</summary>
    public required long LastSyncGeneration { get; init; }
}
