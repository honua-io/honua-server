// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

public enum VersionAccess
{
    Private = 0,

    Protected = 1,

    Public = 2,
}

public enum VersionState
{
    Active = 0,

    Reconciling = 1,

    Posting = 2,

    Deleted = 3,
}

// The default version is never stored as a row, so non-versioned paths are not given a version record.
public readonly record struct GdbVersion
{
    public required Guid VersionId { get; init; }

    public required string VersionName { get; init; }

    public required string Owner { get; init; }

    public Guid? ParentVersion { get; init; }

    public required VersionAccess Access { get; init; }

    public required VersionState State { get; init; }

    public required long CommonAncestorGeneration { get; init; }

    public required long BranchGeneration { get; init; }

    public string? Description { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ModifiedAt { get; init; }
}

public readonly record struct VersionContext
{
    // Read and write paths treat this as the non-versioned base path.
    public static VersionContext Default => new() { VersionId = null };

    public Guid? VersionId { get; init; }

    public string? VersionName { get; init; }

    public long CommonAncestorGeneration { get; init; }

    public bool IsDefault => VersionId is null;

    public static VersionContext ForVersion(GdbVersion version) => new()
    {
        VersionId = version.VersionId,
        VersionName = version.VersionName,
        CommonAncestorGeneration = version.CommonAncestorGeneration,
    };
}
