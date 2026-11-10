// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

public sealed record GeoservicesImportProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }

    public required GeoservicesImportStatus Status { get; init; }

    public int FeaturesProcessed { get; init; }

    public int? EstimatedTotalFeatures { get; init; }

    public int BatchesCompleted { get; init; }

    public int? TotalBatches { get; init; }

    public int FailedFeatures { get; init; }

    public int AttachmentsProcessed { get; init; }

    public int FailedAttachments { get; init; }

    public double? PercentComplete => EstimatedTotalFeatures > 0
        ? (double)FeaturesProcessed / EstimatedTotalFeatures * 100
        : null;

    public required string SourceServiceUrl { get; init; }

    public string SourceKind { get; init; } = MigrationSourceKinds.GeoServicesRest;

    public string SourceUrl => SourceServiceUrl;

    public required int SourceLayerId { get; init; }

    public string? SourceLayerName { get; init; }

    public required string TableName { get; init; }

    public string? ServiceName { get; init; }

    public int? PublishedLayerId { get; init; }

    public int? LayerId => PublishedLayerId;

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    public MigrationReconciliationArtifact? ReconciliationArtifact { get; init; }

    public MigrationCatalogReconciliationReport? CatalogReconciliationReport { get; init; }

    public string? FidelityVerdict { get; init; }

    public MigrationFidelityDifference[] FidelityDifferences { get; init; } = [];

    // IOperationProgress implementation
    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.ExternalImport;
    OperationStatus IOperationProgress.Status => Status switch
    {
        GeoservicesImportStatus.Queued => OperationStatus.Queued,
        GeoservicesImportStatus.Discovering => OperationStatus.Processing,
        GeoservicesImportStatus.RetrievingFeatures => OperationStatus.Processing,
        GeoservicesImportStatus.CreatingTable => OperationStatus.Processing,
        GeoservicesImportStatus.InsertingFeatures => OperationStatus.Processing,
        GeoservicesImportStatus.Publishing => OperationStatus.Processing,
        GeoservicesImportStatus.CopyingAttachments => OperationStatus.Processing,
        GeoservicesImportStatus.Validating => OperationStatus.Processing,
        GeoservicesImportStatus.Completed => OperationStatus.Completed,
        // NeedsReview is a terminal, non-success outcome: the run published data but a hard
        // reconciliation finding blocked completion. The generic operation surface has no
        // dedicated "needs review" state, so it projects as Failed (did not succeed); migration
        // -aware clients read the precise GeoservicesImportStatus.NeedsReview instead.
        GeoservicesImportStatus.NeedsReview => OperationStatus.Failed,
        GeoservicesImportStatus.Failed => OperationStatus.Failed,
        GeoservicesImportStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = GeoservicesImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    public static GeoservicesImportProgress CreateInitial(
        string jobId,
        string sourceServiceUrl,
        int sourceLayerId,
        string tableName,
        string? sourceLayerName = null,
        int? estimatedTotalFeatures = null) =>
        new()
        {
            JobId = jobId,
            Status = GeoservicesImportStatus.Queued,
            SourceServiceUrl = sourceServiceUrl,
            SourceLayerId = sourceLayerId,
            SourceLayerName = sourceLayerName,
            TableName = tableName,
            EstimatedTotalFeatures = estimatedTotalFeatures,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for processing"
        };
}

public enum GeoservicesImportStatus
{
    Queued,

    Discovering,

    RetrievingFeatures,

    CreatingTable,

    InsertingFeatures,

    Publishing,

    CopyingAttachments,

    Validating,

    Completed,

    NeedsReview,

    Failed,

    Cancelled
}
