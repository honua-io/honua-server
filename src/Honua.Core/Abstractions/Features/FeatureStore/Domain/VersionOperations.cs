// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Core.Features.FeatureStore.Domain;

public readonly record struct CreateVersionRequest(
    string VersionName,
    string Owner,
    VersionAccess Access,
    Guid? ParentVersion = null,
    string? Description = null);

public readonly record struct AlterVersionRequest(
    Guid VersionId,
    string? VersionName = null,
    VersionAccess? Access = null,
    string? Description = null);

public enum VersionReconcilePolicy
{
    None = 0,

    LastWriteWins = 1,

    VersionWins = 2,

    DefaultWins = 3,
}

public enum VersionConflictDetection
{
    ByAttribute = 0,

    ByObject = 1,
}

public enum VersionConflictResolutionChoice
{
    TakeVersion = 0,

    TakeDefault = 1,

    TakeBase = 2,
}

public readonly record struct VersionConflictFieldDiff(
    string Name,
    string? Base,
    string? Default,
    string? Version);

public readonly record struct VersionReconcileConflict(
    int LayerId,
    long ObjectId,
    ReplicaConflictType ConflictType,
    string? BaseAttributesJson = null,
    string? DefaultAttributesJson = null,
    string? VersionAttributesJson = null,
    string? BaseGeometryWkt = null,
    string? DefaultGeometryWkt = null,
    string? VersionGeometryWkt = null,
    ImmutableArray<VersionConflictFieldDiff> FieldDiffs = default);

public readonly record struct VersionReconcileResult(
    ImmutableArray<VersionReconcileConflict> Conflicts,
    bool CanPost,
    long NewCommonAncestorGeneration,
    int AutoResolvedCount = 0);

public readonly record struct VersionConflictResolution(
    int LayerId,
    long ObjectId,
    VersionConflictResolutionChoice Choice);

public readonly record struct VersionConflictResolutionResult(
    int Resolved,
    int Remaining,
    bool CanPost);

public readonly record struct VersionPostResult(
    bool Posted,
    int AppliedChanges,
    long ServerGeneration,
    bool BlockedByConflicts);
