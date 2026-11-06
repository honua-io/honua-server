// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public enum ReplicaSyncDirection
{
    Download = 0,

    Upload = 1,

    Bidirectional = 2,
}

public readonly record struct ReplicaUploadEdit(
    FeatureEditOperationKind Kind,
    long? ObjectId,
    object? Payload);

public readonly record struct ReplicaUploadLayerEdits(
    int PublicLayerId,
    int StorageLayerId,
    ImmutableArray<ReplicaUploadEdit> Edits);

public readonly record struct ReplicaSyncRequest(
    string ReplicaId,
    string ServiceId,
    ReplicaSyncDirection Direction,
    long BaseGeneration,
    ImmutableArray<ReplicaUploadLayerEdits> LayerEdits,
    bool LastWriteWins = true,
    string? SyncOperationId = null,
    string? DeviceId = null,
    string? UserId = null,
    bool RollbackOnFailure = false);

// Object identity is not enough when one payload carries several operations for the same object, so the conflict names its position in the upload.
public readonly record struct ReplicaSyncConflict(
    int PublicLayerId,
    long ObjectId,
    ReplicaConflictType ConflictType,
    FeatureEditOperationKind ClientKind,
    FeatureChangeOperation ServerOperation,
    bool Applied,
    string? ConflictId,
    int EditIndex = -1);

// One edit in a batch can commit while a sibling fails, so outcomes are named by request slot.
public readonly record struct ReplicaLayerApplyResult(
    int PublicLayerId,
    int AppliedAdds,
    int AppliedUpdates,
    int AppliedDeletes,
    bool Failed,
    string? FailureMessage,
    ImmutableArray<int> CommittedEditIndexes = default,
    ImmutableArray<int> IndeterminateEditIndexes = default);

public readonly record struct ReplicaSyncUploadReport(
    bool Success,
    int AppliedAdds,
    int AppliedUpdates,
    int AppliedDeletes,
    ImmutableArray<ReplicaSyncConflict> Conflicts,
    ImmutableArray<ReplicaLayerApplyResult> LayerResults,
    long ServerGeneration,
    string? FailureMessage,
    IReadOnlyDictionary<(int PublicLayerId, long ObjectId), string>? ServerStates = null);
