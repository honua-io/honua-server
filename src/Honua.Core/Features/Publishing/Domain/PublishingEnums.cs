// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Publishing.Domain;

/// <summary>
/// Category of the source being published.
/// </summary>
public enum PublishSourceKind
{
    /// <summary>
    /// An <see cref="Geoprocessing.Domain.AnalysisResultPackage"/> produced by a geoprocessing workflow.
    /// </summary>
    ResultPackage,

    /// <summary>
    /// An artifact from an operator workspace.
    /// </summary>
    WorkspaceArtifact,

    /// <summary>
    /// A feature layer registered in the catalog.
    /// </summary>
    FeatureLayer,

    /// <summary>
    /// A raster dataset registered in the catalog.
    /// </summary>
    RasterDataset
}

/// <summary>
/// Category of the target surface for publishing.
/// </summary>
public enum PublishTargetKind
{
    /// <summary>
    /// A hosted feature service endpoint.
    /// </summary>
    FeatureService,

    /// <summary>
    /// A hosted tile service endpoint.
    /// </summary>
    TileService,

    /// <summary>
    /// A hosted map service endpoint.
    /// </summary>
    MapService,

    /// <summary>
    /// A static file export or download surface.
    /// </summary>
    StaticExport,

    /// <summary>
    /// A hosted 3D Tiles scene generated from a feature layer.
    /// </summary>
    SceneService
}

/// <summary>
/// Lifecycle status of a publish intent.
/// </summary>
public enum PublishIntentStatus
{
    /// <summary>
    /// Intent created but not yet validated.
    /// </summary>
    Draft,

    /// <summary>
    /// Intent validated against source and target constraints.
    /// </summary>
    Validated,

    /// <summary>
    /// Intent requires explicit approval before execution.
    /// </summary>
    AwaitingApproval,

    /// <summary>
    /// Intent approved and ready for execution.
    /// </summary>
    Approved,

    /// <summary>
    /// Publishing pipeline is actively executing.
    /// </summary>
    Executing,

    /// <summary>
    /// Publishing completed and a published service record was created.
    /// </summary>
    Completed,

    /// <summary>
    /// Intent was rejected during the approval step.
    /// </summary>
    Rejected,

    /// <summary>
    /// Publishing pipeline execution failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Intent was cancelled before or during execution.
    /// </summary>
    Cancelled
}

/// <summary>
/// Lifecycle status of a published service.
/// </summary>
public enum PublishedServiceStatus
{
    /// <summary>
    /// Service is being provisioned or materialized.
    /// </summary>
    Provisioning,

    /// <summary>
    /// Service is active and serving requests.
    /// </summary>
    Active,

    /// <summary>
    /// Service has been temporarily suspended.
    /// </summary>
    Suspended,

    /// <summary>
    /// The most recent refresh attempt failed.
    /// </summary>
    RefreshFailed,

    /// <summary>
    /// Service has been permanently decommissioned.
    /// </summary>
    Decommissioned
}

/// <summary>
/// How refresh operations are triggered for a published service.
/// </summary>
public enum RefreshMode
{
    /// <summary>
    /// Refresh is triggered only by explicit operator action.
    /// </summary>
    Manual,

    /// <summary>
    /// Refresh runs on a configured schedule.
    /// </summary>
    Scheduled
}

/// <summary>
/// Scope of a refresh operation.
/// </summary>
public enum RefreshScope
{
    /// <summary>
    /// Replace all published content from the source.
    /// </summary>
    Full,

    /// <summary>
    /// Apply only changes since the last successful refresh.
    /// </summary>
    Incremental
}

/// <summary>
/// Outcome of a refresh operation.
/// </summary>
public enum RefreshOutcome
{
    /// <summary>
    /// Refresh completed successfully with all items updated.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Refresh completed but some items could not be updated.
    /// </summary>
    PartialSuccess,

    /// <summary>
    /// Refresh failed entirely.
    /// </summary>
    Failed,

    /// <summary>
    /// Refresh was cancelled before completion.
    /// </summary>
    Cancelled
}
