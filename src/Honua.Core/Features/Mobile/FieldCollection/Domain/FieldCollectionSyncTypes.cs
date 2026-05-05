// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

/// <summary>
/// FieldCollection change operation types. Wire values mirror the mobile
/// <c>ChangeOperation</c> enum so server payloads round-trip without translation.
/// </summary>
public enum FieldCollectionChangeOperation : short
{
    Insert = 1,
    Update = 2,
    Delete = 3,
}

/// <summary>
/// Outcome of a single FieldCollection push operation.
/// </summary>
public enum FieldCollectionPushOutcome : short
{
    Applied = 1,
    Conflict = 2,
    Rejected = 3,
}

/// <summary>
/// Conflict classification returned to clients when a push cannot be applied.
/// Wire values mirror the mobile <c>ConflictType</c> enum.
/// </summary>
public enum FieldCollectionConflictType : short
{
    None = 0,
    UpdateUpdate = 1,
    UpdateDelete = 2,
    DeleteUpdate = 3,
    DeleteDelete = 4,
}

/// <summary>
/// A single FieldCollection feature change replayed to mobile clients during pull.
/// </summary>
public sealed record FieldCollectionChange
{
    public required long Generation { get; init; }
    public required string FeatureId { get; init; }
    public required int LayerId { get; init; }
    public required FieldCollectionChangeOperation Operation { get; init; }
    public required long Version { get; init; }
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
    public required IReadOnlyList<FieldCollectionChange> Changes { get; init; }
    public required long ServerGeneration { get; init; }
    public required long NextCursor { get; init; }
    public required bool HasMore { get; init; }
}

/// <summary>
/// Inbound request describing a single mobile-pushed FieldCollection change.
/// </summary>
public sealed record FieldCollectionPushRequest
{
    public required string ChangeId { get; init; }
    public required string FeatureId { get; init; }
    public required int LayerId { get; init; }
    public required FieldCollectionChangeOperation Operation { get; init; }

    /// <summary>
    /// Server version that the client believes is the parent of this change.
    /// Required for update and delete operations to detect conflicts.
    /// Null is permitted only for inserts.
    /// </summary>
    public long? BaseVersion { get; init; }

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
    public required string ChangeId { get; init; }
    public required FieldCollectionPushOutcome Outcome { get; init; }
    public required long ServerGeneration { get; init; }

    /// <summary>
    /// Server-assigned version after applying the change. Null when the push was not applied.
    /// </summary>
    public long? Version { get; init; }

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
    public required string ClientId { get; init; }
    public required long LastSyncGeneration { get; init; }
}
