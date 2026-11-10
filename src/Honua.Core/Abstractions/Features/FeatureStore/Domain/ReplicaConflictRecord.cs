// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public enum ReplicaConflictType
{
    Attribute = 0,

    Geometry = 1,

    DeleteUpdate = 2,

    UpdateDelete = 3,

    DuplicateInsert = 4,

    Attachment = 5,

    Relationship = 6,

    DeleteDelete = 7,
}

public enum ReplicaConflictStatus
{
    Pending = 0,

    Resolved = 1,

    Deferred = 2,
}

public enum ReplicaConflictResolutionAction
{
    AcceptClient = 0,

    KeepServer = 1,

    MergeFields = 2,

    ChooseGeometry = 3,

    RejectClient = 4,

    Defer = 5,
}

public readonly record struct ReplicaConflictRecord
{
    public required string ConflictId { get; init; }

    public required string ReplicaId { get; init; }

    public required string ServiceId { get; init; }

    public required int LayerId { get; init; }

    public int? StorageLayerId { get; init; }

    public long? StorageObjectId { get; init; }

    public required long ObjectId { get; init; }

    public required ReplicaConflictType ConflictType { get; init; }

    public required ReplicaConflictStatus Status { get; init; }

    public string? SyncOperationId { get; init; }

    public string? DeviceId { get; init; }

    public string? UserId { get; init; }

    public required long ServerGeneration { get; init; }

    // A change after this generation is a newer edit, so a late resolution is unsafe.
    public long? ResolutionBaseGeneration { get; init; }

    // The write flag is set before finalization so a retry does not apply the same write twice.
    public bool WriteCommitted { get; init; }

    // A resume must carry the same resolution inputs, or it would finalize one write while reporting another.
    public string? ResolutionInputHash { get; init; }

    // Recovery re-applies with the token captured at claim time, because a token read at retry would describe a later foreign edit.
    public string? PreWriteStateToken { get; init; }

    // True means the row was absent at claim, so recovery uses an expected-absence precondition.
    public bool? PreWriteRowAbsent { get; init; }

    // A pending resolution is resumed rather than treated as already finished, so an interrupted write cannot drop its generation or audit.
    public bool FinalizationPending { get; init; }

    public bool ClientEditApplied { get; init; }

    // When the client commit is unknown, neither no-op shortcut is safe.
    public bool ClientEditOutcomeUnknown { get; init; }

    // The row holds a later edit from the same upload, so keeping the server must restore rather than skip.
    public bool ClientEditSuperseded { get; init; }

    // The base image stays null when the change log has no value snapshot, so the review stays a two-way diff.
    public string? BaseStateJson { get; init; }

    public string? ClientStateJson { get; init; }

    public string? ServerStateJson { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }

    public ReplicaConflictResolutionAction? ResolutionAction { get; init; }

    public string? ResolvedBy { get; init; }

    public DateTimeOffset? ResolvedAt { get; init; }

    public long? ResolvedServerGeneration { get; init; }
}
