// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;

namespace Honua.Core.Features.FeatureStore.Domain;

public enum ReplicaConflictResolutionEffect
{
    None = 0,

    WriteFeatureState = 1,

    DeleteFeature = 2,
}

public enum ReplicaConflictResolutionRejection
{
    None = 0,

    InvalidRequest = 1,

    NotApplicable = 2,
}

public readonly record struct ReplicaConflictResolutionInputs(
    IReadOnlyDictionary<string, JsonElement>? FieldValues,
    string? GeometrySource);

public readonly record struct ReplicaConflictResolutionPlan(
    ReplicaConflictResolutionEffect Effect,
    string? FeatureStateJson,
    bool CommittedNewServerState,
    ReplicaConflictResolutionRejection Rejection,
    string? RejectionMessage)
{
    public bool IsAccepted => Rejection == ReplicaConflictResolutionRejection.None;

    public bool ReplaceAttributes { get; init; }

    public ImmutableArray<string> ExplicitAttributeNames { get; init; }
}

public readonly record struct ReplicaConflictResolutionCommand(
    string ServiceId,
    int PublicLayerId,
    long ObjectId,
    ReplicaConflictResolutionEffect Effect,
    string? FeatureStateJson,
    int? StorageLayerId = null,
    string? ExpectedStateToken = null,
    bool ExpectedRowAbsent = false)
{
    public bool ReplaceAttributes { get; init; }

    public ImmutableArray<string> ExplicitAttributeNames { get; init; }
}

public readonly record struct ReplicaConflictApplyResult(
    bool Applied,
    string? FailureMessage,
    bool CommitOutcomeUnknown = false,
    bool PreconditionFailed = false);
