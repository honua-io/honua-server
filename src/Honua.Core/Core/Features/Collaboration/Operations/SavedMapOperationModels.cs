// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Core.Features.Collaboration.Operations;

public readonly record struct SavedMapOperationId(string Value);

public readonly record struct SavedMapActorId(string Value);

public readonly record struct SavedMapId(string Value);

public readonly record struct SavedMapOperationCursor(long Value)
{
    public static SavedMapOperationCursor Empty { get; } = new(0);
}

public enum SavedMapOperationFamily
{
    Metadata,

    Viewport,

    LayerVisibility,

    LayerOrdering,

    Style,

    // A whole-document WebMap update is unsafe when it is based on a stale cursor.
    WebMapDocument
}

public enum SavedMapOperationKind
{
    SetMetadataField,

    SetViewport,

    SetLayerVisibility,

    ReorderLayers,

    PatchStyle,

    ReplaceWebMapDocument
}

public static class SavedMapOperationKinds
{
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

public sealed record SavedMapOperationAppendRequest
{
    public required SavedMapOperationId OperationId { get; init; }

    public required SavedMapId MapId { get; init; }

    public required SavedMapActorId ActorId { get; init; }

    public required SavedMapOperationCursor BaseCursor { get; init; }

    public required SavedMapOperationKind Kind { get; init; }

    public JsonElement Payload { get; init; }

    public string? IdempotencyKey { get; init; }
}

public sealed record SavedMapOperationEnvelope
{
    public required SavedMapOperationId OperationId { get; init; }

    public required SavedMapId MapId { get; init; }

    public required SavedMapActorId ActorId { get; init; }

    public required SavedMapOperationCursor BaseCursor { get; init; }

    public required SavedMapOperationKind Kind { get; init; }

    public SavedMapOperationFamily Family => Kind.GetFamily();

    public JsonElement Payload { get; init; }

    public string? IdempotencyKey { get; init; }

    public required SavedMapOperationCursor ServerCursor { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}

public enum SavedMapOperationAppendStatus
{
    Accepted,

    Conflict,

    ResyncRequired,

    IdempotencyMismatch
}

public enum SavedMapOperationReplayStatus
{
    Ok,

    // The caller must resync from a full saved-map document snapshot.
    ResyncRequired
}

public sealed record SavedMapOperationConflictResponse
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required SavedMapOperationCursor BaseCursor { get; init; }

    public required SavedMapOperationCursor HeadCursor { get; init; }

    public required IReadOnlyList<SavedMapOperationEnvelope> ConflictingOperations { get; init; }
}

public sealed record SavedMapOperationAppendResult
{
    public required SavedMapOperationAppendStatus Status { get; init; }

    public SavedMapOperationEnvelope? Operation { get; init; }

    public SavedMapOperationConflictResponse? Conflict { get; init; }

    public required SavedMapOperationCursor HeadCursor { get; init; }

    public bool IsDuplicate { get; init; }

    public string? Message { get; init; }
}

public sealed record SavedMapOperationReplayResult
{
    public required SavedMapOperationReplayStatus Status { get; init; }

    public required SavedMapOperationCursor SinceCursor { get; init; }

    public required SavedMapOperationCursor HeadCursor { get; init; }

    public required SavedMapOperationCursor MinimumReplayCursor { get; init; }

    public required IReadOnlyList<SavedMapOperationEnvelope> Operations { get; init; }

    public string? Message { get; init; }
}
