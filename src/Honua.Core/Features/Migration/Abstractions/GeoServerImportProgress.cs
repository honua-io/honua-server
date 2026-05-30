// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Progress information for a GeoServer import operation.
/// </summary>
public sealed record GeoServerImportProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this import job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the import.
    /// </summary>
    public required GeoServerImportStatus Status { get; init; }

    /// <summary>
    /// Number of resources (workspaces, datastores, layers, styles) processed so far.
    /// </summary>
    public int ResourcesProcessed { get; init; }

    /// <summary>
    /// Estimated total number of resources to process.
    /// </summary>
    public int? EstimatedTotalResources { get; init; }

    /// <summary>
    /// Number of resources that failed to import.
    /// </summary>
    public int FailedResources { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete => EstimatedTotalResources > 0
        ? (double)ResourcesProcessed / EstimatedTotalResources * 100
        : null;

    /// <summary>
    /// Source GeoServer REST API URL.
    /// </summary>
    public required string SourceGeoServerUrl { get; init; }

    /// <summary>
    /// Source GeoServer version information.
    /// </summary>
    public string? SourceGeoServerVersion { get; init; }

    /// <summary>
    /// Target Honua server URL.
    /// </summary>
    public required string TargetHonuaUrl { get; init; }

    /// <summary>
    /// When the import started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the import completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the import operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// List of warning messages encountered during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    /// <summary>
    /// Detailed breakdown of processed resources by type.
    /// </summary>
    public GeoServerImportResourceBreakdown? ResourceBreakdown { get; init; }

    /// <summary>
    /// Deterministic apply plan generated for non-dry-run imports.
    /// </summary>
    public MigrationApplyPlanArtifact? ApplyPlan { get; init; }

    /// <summary>
    /// Deterministic execution evidence for non-dry-run apply-plan processing.
    /// </summary>
    public MigrationApplyExecutionArtifact? ApplyExecution { get; init; }

    /// <summary>
    /// Raw cost and performance metrics emitted by the migration pipeline.
    /// </summary>
    public MigrationRunMetricsArtifact? RunMetrics { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.ExternalImport;
    OperationStatus IOperationProgress.Status => Status switch
    {
        GeoServerImportStatus.Queued => OperationStatus.Queued,
        GeoServerImportStatus.Discovering => OperationStatus.Processing,
        GeoServerImportStatus.ImportingWorkspaces => OperationStatus.Processing,
        GeoServerImportStatus.ImportingDataStores => OperationStatus.Processing,
        GeoServerImportStatus.ImportingLayers => OperationStatus.Processing,
        GeoServerImportStatus.ImportingStyles => OperationStatus.Processing,
        GeoServerImportStatus.Validating => OperationStatus.Processing,
        GeoServerImportStatus.Completed => OperationStatus.Completed,
        GeoServerImportStatus.Failed => OperationStatus.Failed,
        GeoServerImportStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = GeoServerImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new GeoServer import job.
    /// </summary>
    public static GeoServerImportProgress CreateInitial(
        string jobId,
        string sourceGeoServerUrl,
        string targetHonuaUrl,
        int? estimatedTotalResources = null,
        string? sourceGeoServerVersion = null) =>
        new()
        {
            JobId = jobId,
            Status = GeoServerImportStatus.Queued,
            SourceGeoServerUrl = sourceGeoServerUrl,
            TargetHonuaUrl = targetHonuaUrl,
            SourceGeoServerVersion = sourceGeoServerVersion,
            EstimatedTotalResources = estimatedTotalResources,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for processing"
        };
}

/// <summary>
/// Status of a GeoServer import operation.
/// </summary>
public enum GeoServerImportStatus
{
    /// <summary>
    /// Import is queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Discovering GeoServer configuration and resources.
    /// </summary>
    Discovering,

    /// <summary>
    /// Importing workspaces from GeoServer.
    /// </summary>
    ImportingWorkspaces,

    /// <summary>
    /// Importing datastores from GeoServer.
    /// </summary>
    ImportingDataStores,

    /// <summary>
    /// Importing layers from GeoServer.
    /// </summary>
    ImportingLayers,

    /// <summary>
    /// Importing styles from GeoServer (requires issue #375).
    /// </summary>
    ImportingStyles,

    /// <summary>
    /// Validating imported configuration.
    /// </summary>
    Validating,

    /// <summary>
    /// Import completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Import failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Import was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Breakdown of resources processed during GeoServer import.
/// </summary>
public sealed record GeoServerImportResourceBreakdown
{
    /// <summary>
    /// Number of workspaces processed.
    /// </summary>
    public int WorkspacesProcessed { get; init; }

    /// <summary>
    /// Number of workspaces that failed.
    /// </summary>
    public int WorkspacesFailed { get; init; }

    /// <summary>
    /// Number of datastores processed.
    /// </summary>
    public int DataStoresProcessed { get; init; }

    /// <summary>
    /// Number of datastores that failed.
    /// </summary>
    public int DataStoresFailed { get; init; }

    /// <summary>
    /// Number of layers processed.
    /// </summary>
    public int LayersProcessed { get; init; }

    /// <summary>
    /// Number of layers that failed.
    /// </summary>
    public int LayersFailed { get; init; }

    /// <summary>
    /// Number of styles processed.
    /// </summary>
    public int StylesProcessed { get; init; }

    /// <summary>
    /// Number of styles that failed (may require manual conversion).
    /// </summary>
    public int StylesFailed { get; init; }
}
