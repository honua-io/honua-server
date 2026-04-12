// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Geoprocessing;

/// <summary>
/// Configuration options for workspace lifecycle management.
/// </summary>
public sealed class WorkspaceOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Geoprocessing:Workspace";

    /// <summary>
    /// How frequently the cleanup service runs. Default: 15 minutes.
    /// </summary>
    public TimeSpan CleanupInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Grace period after expiration before a workspace is deleted. Default: 1 hour.
    /// Allows promotion of artifacts from recently-expired workspaces.
    /// </summary>
    public TimeSpan CleanupGracePeriod { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Whether the automatic cleanup background service is enabled.
    /// </summary>
    public bool EnableAutomaticCleanup { get; init; } = true;

    /// <summary>
    /// Maximum number of workspaces to process per cleanup sweep to bound memory usage.
    /// </summary>
    public int MaxCleanupBatchSize { get; init; } = 100;

    /// <summary>
    /// Default time-to-live override for scratch workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? ScratchDefaultTtl { get; init; }

    /// <summary>
    /// Default time-to-live override for temp layer workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? TempLayerDefaultTtl { get; init; }

    /// <summary>
    /// Default time-to-live override for result collection workspaces. Null uses the built-in default.
    /// </summary>
    public TimeSpan? ResultCollectionDefaultTtl { get; init; }

    /// <summary>
    /// Maximum workspace count per owner. Null uses the built-in default.
    /// </summary>
    public int? MaxWorkspaceCount { get; init; }

    /// <summary>
    /// Maximum artifact count per owner. Null uses the built-in default.
    /// </summary>
    public int? MaxArtifactCount { get; init; }

    /// <summary>
    /// Maximum storage bytes per owner. Null uses the built-in default.
    /// </summary>
    public long? MaxStorageBytes { get; init; }
}
