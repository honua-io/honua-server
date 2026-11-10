// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

public sealed record GeoServerImportProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }

    public required GeoServerImportStatus Status { get; init; }

    public int ResourcesProcessed { get; init; }

    public int? EstimatedTotalResources { get; init; }

    public int FailedResources { get; init; }

    public double? PercentComplete => EstimatedTotalResources > 0
        ? (double)ResourcesProcessed / EstimatedTotalResources * 100
        : null;

    public required string SourceGeoServerUrl { get; init; }

    public string? SourceGeoServerVersion { get; init; }

    public required string TargetHonuaUrl { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    public GeoServerImportResourceBreakdown? ResourceBreakdown { get; init; }

    public MigrationApplyPlanArtifact? ApplyPlan { get; init; }

    public MigrationApplyExecutionArtifact? ApplyExecution { get; init; }

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

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = GeoServerImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

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

public enum GeoServerImportStatus
{
    Queued,

    Discovering,

    ImportingWorkspaces,

    ImportingDataStores,

    ImportingLayers,

    ImportingStyles,

    Validating,

    Completed,

    Failed,

    Cancelled
}

public sealed record GeoServerImportResourceBreakdown
{
    public int WorkspacesProcessed { get; init; }

    public int WorkspacesFailed { get; init; }

    public int DataStoresProcessed { get; init; }

    public int DataStoresFailed { get; init; }

    public int LayersProcessed { get; init; }

    public int LayersFailed { get; init; }

    public int StylesProcessed { get; init; }

    public int StylesFailed { get; init; }
}
