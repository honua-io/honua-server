// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public enum VersionJobKind
{
    Reconcile = 0,

    Post = 1,
}

public enum VersionJobStatus
{
    Pending = 0,

    Running = 1,

    Succeeded = 2,

    Failed = 3,

    LockContended = 4,
}

public sealed record VersionJob(
    Guid JobId,
    string Service,
    Guid VersionId,
    VersionJobKind Kind,
    VersionJobStatus Status,
    VersionReconcilePolicy Policy,
    DateTimeOffset CreatedAt,
    VersionConflictDetection ConflictDetection = VersionConflictDetection.ByAttribute,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    int ConflictCount = 0,
    int AutoResolvedCount = 0,
    bool CanPost = false,
    int AppliedChanges = 0,
    long ServerGeneration = 0,
    bool BlockedByConflicts = false,
    string? ErrorMessage = null);
