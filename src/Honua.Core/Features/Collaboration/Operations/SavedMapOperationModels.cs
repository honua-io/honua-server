// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Collaboration.Operations;

/// <summary>
/// Stable client-generated identifier for a saved-map edit operation.
/// </summary>
public readonly record struct SavedMapOperationId(string Value);

/// <summary>
/// Stable identifier for the actor that submitted a saved-map edit operation.
/// </summary>
public readonly record struct SavedMapActorId(string Value);

/// <summary>
/// Stable identifier for the saved map whose collaborative edit log is being mutated.
/// </summary>
public readonly record struct SavedMapId(string Value);

/// <summary>
/// Monotonic server-assigned saved-map operation cursor. Cursor <c>0</c> is the empty-log base.
/// </summary>
public readonly record struct SavedMapOperationCursor(long Value)
{
    /// <summary>
    /// Empty-log cursor used by clients with no prior operation state.
    /// </summary>
    public static SavedMapOperationCursor Empty { get; } = new(0);
}

/// <summary>
/// Saved-map operation families used by the MVP conflict policy.
/// </summary>
public enum SavedMapOperationFamily
{
    /// <summary>
    /// Map metadata fields that can be reconciled independently.
    /// </summary>
    Metadata,

    /// <summary>
    /// Viewport and camera fields that are safe to append after concurrent safe operations.
    /// </summary>
    Viewport,

    /// <summary>
    /// Layer visibility toggles that are safe to append after concurrent safe operations.
    /// </summary>
    LayerVisibility,

    /// <summary>
    /// Layer ordering operations that are safe to append after concurrent safe operations.
    /// </summary>
    LayerOrdering,

    /// <summary>
    /// Style patch operations that are safe to append after concurrent safe operations.
    /// </summary>
    Style,

    /// <summary>
    /// Whole-document WebMap updates. These are unsafe when based on a stale cursor.
    /// </summary>
    WebMapDocument
}

/// <summary>
/// Saved-map edit operation kinds supported by the operation-log seam.
/// </summary>
public enum SavedMapOperationKind
{
    /// <summary>
    /// Set a saved-map title or metadata field.
    /// </summary>
    SetMetadataField,

    /// <summary>
    /// Set the saved-map viewport or camera state.
    /// </summary>
    SetViewport,

    /// <summary>
    /// Set visibility for one layer.
    /// </summary>
    SetLayerVisibility,

    /// <summary>
    /// Reorder saved-map layers.
    /// </summary>
    ReorderLayers,

    /// <summary>
    /// Patch styling for one layer or style target.
    /// </summary>
    PatchStyle,

    /// <summary>
    /// Replace the full WebMap document. This is intentionally modeled as an unsafe family.
    /// </summary>
    ReplaceWebMapDocument
}

/// <summary>
/// Helpers for mapping saved-map operation kinds onto the documented MVP operation families.
/// </summary>
public static class SavedMapOperationKinds
{
    /// <summary>
    /// Gets the operation family used for conflict detection.
    /// </summary>
    public static SavedMapOperationFamily GetFamily(this SavedMapOperationKind kind) => kind switch
    {
        SavedMapOperationKind.SetMetadataField => SavedMapOperationFamily.Metadata,
        SavedMapOperationKind.SetViewport => SavedMapOperationFamily.Viewport,
        SavedMapOperationKind.SetLayerVisibility => SavedMapOperationFamily.LayerVisibility,
        SavedMapOperationKind.ReorderLayers => SavedMapOperationFamily.LayerOrdering,
        SavedMapOperationKind.PatchStyle => SavedMapOperationFamily.Style,
        SavedMapOperationKind.ReplaceWebMapDocument => SavedMapOperationFamily.WebMapDocument,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown saved-map operation kind.")
    };
}

/// <summary>
/// Client request to append one saved-map edit operation.
/// </summary>
public sealed record SavedMapOperationAppendRequest
{
    /// <summary>
    /// Client-generated operation identifier. Duplicates are treated idempotently per map.
    /// </summary>
    public required SavedMapOperationId OperationId { get; init; }

    /// <summary>
    /// Saved map being edited.
    /// </summary>
    public required SavedMapId MapId { get; init; }

    /// <summary>
    /// Actor submitting the operation.
    /// </summary>
    public required SavedMapActorId ActorId { get; init; }

    /// <summary>
    /// Client's base cursor/revision for this edit.
    /// </summary>
    public required SavedMapOperationCursor BaseCursor { get; init; }

    /// <summary>
    /// Operation kind used to route future WebMapDoc integration and MVP conflict policy.
    /// </summary>
    public required SavedMapOperationKind Kind { get; init; }

    /// <summary>
    /// Operation payload. The MVP log stores the JSON envelope without interpreting WebMapDoc fields.
    /// </summary>
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Optional caller idempotency key. When supplied, duplicates are treated idempotently per map.
    /// </summary>
    public string? IdempotencyKey { get; init; }
}

/// <summary>
/// Server-accepted saved-map edit operation envelope.
/// </summary>
public sealed record SavedMapOperationEnvelope
{
    /// <summary>
    /// Client-generated operation identifier.
    /// </summary>
    public required SavedMapOperationId OperationId { get; init; }

    /// <summary>
    /// Saved map being edited.
    /// </summary>
    public required SavedMapId MapId { get; init; }

    /// <summary>
    /// Actor that submitted the operation.
    /// </summary>
    public required SavedMapActorId ActorId { get; init; }

    /// <summary>
    /// Client base cursor/revision used when the operation was appended.
    /// </summary>
    public required SavedMapOperationCursor BaseCursor { get; init; }

    /// <summary>
    /// Operation kind.
    /// </summary>
    public required SavedMapOperationKind Kind { get; init; }

    /// <summary>
    /// Operation family used by conflict detection.
    /// </summary>
    public SavedMapOperationFamily Family => Kind.GetFamily();

    /// <summary>
    /// Operation payload.
    /// </summary>
    public JsonElement Payload { get; init; }

    /// <summary>
    /// Optional idempotency key supplied by the caller.
    /// </summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>
    /// Monotonic server cursor assigned to this operation.
    /// </summary>
    public required SavedMapOperationCursor ServerCursor { get; init; }

    /// <summary>
    /// Server timestamp captured when the operation was accepted.
    /// </summary>
    public required DateTimeOffset AcceptedAt { get; init; }
}

/// <summary>
/// Append result status for saved-map operation log writes.
/// </summary>
public enum SavedMapOperationAppendStatus
{
    /// <summary>
    /// Operation was accepted, or a duplicate idempotent operation was replayed.
    /// </summary>
    Accepted,

    /// <summary>
    /// Operation conflicts with concurrent edits and must be resolved by the caller.
    /// </summary>
    Conflict,

    /// <summary>
    /// Caller cursor is outside the replay window or ahead of the current head.
    /// </summary>
    ResyncRequired
}

/// <summary>
/// Replay result status for saved-map operation log reads.
/// </summary>
public enum SavedMapOperationReplayStatus
{
    /// <summary>
    /// Replay succeeded.
    /// </summary>
    Ok,

    /// <summary>
    /// Caller must resync from a full saved-map document snapshot.
    /// </summary>
    ResyncRequired
}

/// <summary>
/// Conflict payload returned when a stale append cannot be safely merged.
/// </summary>
public sealed record SavedMapOperationConflictResponse
{
    /// <summary>
    /// Stable conflict code.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Human-readable conflict message.
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Client base cursor/revision that produced the conflict.
    /// </summary>
    public required SavedMapOperationCursor BaseCursor { get; init; }

    /// <summary>
    /// Current saved-map operation-log head cursor.
    /// </summary>
    public required SavedMapOperationCursor HeadCursor { get; init; }

    /// <summary>
    /// Concurrent operations that made the append unsafe.
    /// </summary>
    public required IReadOnlyList<SavedMapOperationEnvelope> ConflictingOperations { get; init; }
}

/// <summary>
/// Result returned by saved-map operation appends.
/// </summary>
public sealed record SavedMapOperationAppendResult
{
    /// <summary>
    /// Append status.
    /// </summary>
    public required SavedMapOperationAppendStatus Status { get; init; }

    /// <summary>
    /// Accepted operation envelope when <see cref="Status"/> is <see cref="SavedMapOperationAppendStatus.Accepted"/>.
    /// </summary>
    public SavedMapOperationEnvelope? Operation { get; init; }

    /// <summary>
    /// Conflict details when <see cref="Status"/> is <see cref="SavedMapOperationAppendStatus.Conflict"/>.
    /// </summary>
    public SavedMapOperationConflictResponse? Conflict { get; init; }

    /// <summary>
    /// Current saved-map operation-log head cursor.
    /// </summary>
    public required SavedMapOperationCursor HeadCursor { get; init; }

    /// <summary>
    /// Indicates whether an accepted response came from a duplicate operation id or idempotency key.
    /// </summary>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// Message explaining non-accepted results.
    /// </summary>
    public string? Message { get; init; }
}

/// <summary>
/// Result returned by saved-map operation replay reads.
/// </summary>
public sealed record SavedMapOperationReplayResult
{
    /// <summary>
    /// Replay status.
    /// </summary>
    public required SavedMapOperationReplayStatus Status { get; init; }

    /// <summary>
    /// Cursor the caller requested replay after.
    /// </summary>
    public required SavedMapOperationCursor SinceCursor { get; init; }

    /// <summary>
    /// Current saved-map operation-log head cursor.
    /// </summary>
    public required SavedMapOperationCursor HeadCursor { get; init; }

    /// <summary>
    /// Earliest cursor from which this in-memory log can replay.
    /// </summary>
    public required SavedMapOperationCursor MinimumReplayCursor { get; init; }

    /// <summary>
    /// Operations accepted after <see cref="SinceCursor"/>, ordered by server cursor.
    /// </summary>
    public required IReadOnlyList<SavedMapOperationEnvelope> Operations { get; init; }

    /// <summary>
    /// Message explaining resync-required results.
    /// </summary>
    public string? Message { get; init; }
}
