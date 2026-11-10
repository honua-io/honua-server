// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Mobile.FieldCollection.Domain;

public enum FieldCollectionChangeOperation : short
{
    Insert = 1,

    Update = 2,

    Delete = 3,
}

public enum FieldCollectionPushOutcome : short
{
    Applied = 1,

    Conflict = 2,

    Rejected = 3,
}

public enum FieldCollectionConflictType : short
{
    None = 0,

    UpdateUpdate = 1,

    UpdateDelete = 2,

    DeleteUpdate = 3,

    DeleteDelete = 4,
}

public sealed record FieldCollectionChange
{
    public required long Generation { get; init; }

    public required string FeatureId { get; init; }

    public required int LayerId { get; init; }

    public required FieldCollectionChangeOperation Operation { get; init; }

    public required long Version { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public string? FeaturePayloadJson { get; init; }
}

public sealed record FieldCollectionChangesPage
{
    public required IReadOnlyList<FieldCollectionChange> Changes { get; init; }

    public required long ServerGeneration { get; init; }

    public required long NextCursor { get; init; }

    public required bool HasMore { get; init; }
}

public sealed record FieldCollectionPushRequest
{
    public required string ChangeId { get; init; }

    public required string FeatureId { get; init; }

    public required int LayerId { get; init; }

    public required FieldCollectionChangeOperation Operation { get; init; }

    public long? BaseVersion { get; init; }

    public DateTimeOffset? Timestamp { get; init; }

    public string? FeaturePayloadJson { get; init; }
}

public sealed record FieldCollectionPushResult
{
    public required string ChangeId { get; init; }

    public required FieldCollectionPushOutcome Outcome { get; init; }

    public required long ServerGeneration { get; init; }

    public long? Version { get; init; }

    public FieldCollectionConflictType ConflictType { get; init; }

    public string? ServerFeaturePayloadJson { get; init; }

    public long? ServerVersion { get; init; }

    // The detail never includes SQL, a stack, a file path, or a connection string.
    public string? RejectionReason { get; init; }
}

public sealed record FieldCollectionSyncCursor
{
    public required string ClientId { get; init; }

    public required long LastSyncGeneration { get; init; }
}
