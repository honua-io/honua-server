// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

public sealed record RetentionPolicy
{
    public required WorkspaceKind WorkspaceKind { get; init; }

    public TimeSpan? DefaultTimeToLive { get; init; }

    public TimeSpan? MaxTimeToLive { get; init; }

    public bool AllowPromotionBeforeCleanup { get; init; }

    public static IReadOnlyDictionary<WorkspaceKind, RetentionPolicy> Defaults { get; } =
        new Dictionary<WorkspaceKind, RetentionPolicy>
        {
            [WorkspaceKind.Scratch] = new()
            {
                WorkspaceKind = WorkspaceKind.Scratch,
                DefaultTimeToLive = TimeSpan.FromHours(1),
                MaxTimeToLive = TimeSpan.FromHours(24),
                AllowPromotionBeforeCleanup = true
            },
            [WorkspaceKind.TempLayer] = new()
            {
                WorkspaceKind = WorkspaceKind.TempLayer,
                DefaultTimeToLive = TimeSpan.FromHours(24),
                MaxTimeToLive = TimeSpan.FromDays(7),
                AllowPromotionBeforeCleanup = true
            },
            [WorkspaceKind.Persistent] = new()
            {
                WorkspaceKind = WorkspaceKind.Persistent,
                DefaultTimeToLive = null,
                MaxTimeToLive = null,
                AllowPromotionBeforeCleanup = false
            },
            [WorkspaceKind.SavedLayer] = new()
            {
                WorkspaceKind = WorkspaceKind.SavedLayer,
                DefaultTimeToLive = null,
                MaxTimeToLive = null,
                AllowPromotionBeforeCleanup = false
            },
            [WorkspaceKind.ResultCollection] = new()
            {
                WorkspaceKind = WorkspaceKind.ResultCollection,
                DefaultTimeToLive = TimeSpan.FromDays(7),
                MaxTimeToLive = TimeSpan.FromDays(30),
                AllowPromotionBeforeCleanup = true
            }
        };
}

public sealed record WorkspaceQuota
{
    public int? MaxWorkspaceCount { get; init; }

    public int? MaxArtifactCount { get; init; }

    public long? MaxStorageBytes { get; init; }

    public static WorkspaceQuota Default { get; } = new()
    {
        MaxWorkspaceCount = 100,
        MaxArtifactCount = 1000,
        MaxStorageBytes = 10L * 1024 * 1024 * 1024 // 10 GB
    };
}
