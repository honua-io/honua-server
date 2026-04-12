// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// Tracks the lifecycle state of a managed workspace.
/// </summary>
public enum WorkspaceLifecycleState
{
    /// <summary>
    /// Workspace is active and available for use.
    /// </summary>
    Active,

    /// <summary>
    /// Workspace has passed its expiration time and is pending cleanup.
    /// </summary>
    Expired,

    /// <summary>
    /// Workspace has been archived and is no longer directly accessible.
    /// </summary>
    Archived,

    /// <summary>
    /// Workspace has been deleted and its storage reclaimed.
    /// </summary>
    Deleted
}

/// <summary>
/// Tracks the lifecycle state of an artifact within a workspace.
/// </summary>
public enum ArtifactLifecycleState
{
    /// <summary>
    /// Artifact is being created by an in-progress workflow.
    /// </summary>
    Pending,

    /// <summary>
    /// Artifact is materialized and available for access.
    /// </summary>
    Available,

    /// <summary>
    /// Artifact has been promoted to a durable workspace or published destination.
    /// </summary>
    Promoted,

    /// <summary>
    /// Artifact has passed its retention period and is pending cleanup.
    /// </summary>
    Expired,

    /// <summary>
    /// Artifact has been deleted and its storage reclaimed.
    /// </summary>
    Deleted
}
