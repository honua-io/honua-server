// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Retention rules applied to a workspace based on its kind.
/// </summary>
public sealed record RetentionPolicy
{
    /// <summary>
    /// Workspace kind this policy applies to.
    /// </summary>
    public required WorkspaceKind WorkspaceKind { get; init; }

    /// <summary>
    /// Default time-to-live for workspaces of this kind.
    /// Null means no automatic expiration.
    /// </summary>
    public TimeSpan? DefaultTimeToLive { get; init; }

    /// <summary>
    /// Maximum allowed time-to-live for workspaces of this kind, even if explicitly extended.
    /// Null means no upper bound.
    /// </summary>
    public TimeSpan? MaxTimeToLive { get; init; }

    /// <summary>
    /// Whether artifacts in expired workspaces of this kind are eligible for promotion
    /// before cleanup runs.
    /// </summary>
    public bool AllowPromotionBeforeCleanup { get; init; }

    /// <summary>
    /// Returns well-known default retention policies for each workspace kind.
    /// </summary>
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

/// <summary>
/// Quota limits applied to workspace storage for a given scope (user, organization, or global).
/// </summary>
public sealed record WorkspaceQuota
{
    /// <summary>
    /// Maximum number of active workspaces allowed.
    /// Null means unlimited.
    /// </summary>
    public int? MaxWorkspaceCount { get; init; }

    /// <summary>
    /// Maximum total artifact count across all active workspaces.
    /// Null means unlimited.
    /// </summary>
    public int? MaxArtifactCount { get; init; }

    /// <summary>
    /// Maximum total storage bytes across all active workspaces.
    /// Null means unlimited.
    /// </summary>
    public long? MaxStorageBytes { get; init; }

    /// <summary>
    /// Default quota when no explicit quota is configured.
    /// </summary>
    public static WorkspaceQuota Default { get; } = new()
    {
        MaxWorkspaceCount = 100,
        MaxArtifactCount = 1000,
        MaxStorageBytes = 10L * 1024 * 1024 * 1024 // 10 GB
    };
}
