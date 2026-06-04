// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Progress information for a Geoservices import operation.
/// </summary>
public sealed record GeoservicesImportProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this import job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the import.
    /// </summary>
    public required GeoservicesImportStatus Status { get; init; }

    /// <summary>
    /// Number of features processed so far.
    /// </summary>
    public int FeaturesProcessed { get; init; }

    /// <summary>
    /// Estimated total number of features.
    /// </summary>
    public int? EstimatedTotalFeatures { get; init; }

    /// <summary>
    /// Number of batches completed.
    /// </summary>
    public int BatchesCompleted { get; init; }

    /// <summary>
    /// Total number of batches (if known).
    /// </summary>
    public int? TotalBatches { get; init; }

    /// <summary>
    /// Number of features that failed to import.
    /// </summary>
    public int FailedFeatures { get; init; }

    /// <summary>
    /// Number of feature attachments copied so far while the import is in the
    /// <see cref="GeoservicesImportStatus.CopyingAttachments"/> phase.
    /// </summary>
    public int AttachmentsProcessed { get; init; }

    /// <summary>
    /// Number of source attachments that failed to copy.
    /// </summary>
    public int FailedAttachments { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete => EstimatedTotalFeatures > 0
        ? (double)FeaturesProcessed / EstimatedTotalFeatures * 100
        : null;

    /// <summary>
    /// Source ArcGIS service URL.
    /// </summary>
    public required string SourceServiceUrl { get; init; }

    /// <summary>
    /// Stable source kind for admin clients that render mixed import job results.
    /// </summary>
    public string SourceKind { get; init; } = "arcgis-geoservices-rest";

    /// <summary>
    /// Source URL alias used by generic admin import result views.
    /// </summary>
    public string SourceUrl => SourceServiceUrl;

    /// <summary>
    /// Source layer ID.
    /// </summary>
    public required int SourceLayerId { get; init; }

    /// <summary>
    /// Source layer name.
    /// </summary>
    public string? SourceLayerName { get; init; }

    /// <summary>
    /// Target table name.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Target Honua service name when the import requested or completed publishing.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Published Honua layer ID when auto-publish completed successfully.
    /// </summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>
    /// Published layer ID alias used by generic admin import result views.
    /// </summary>
    public int? LayerId => PublishedLayerId;

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
    /// Per-layer data-reconciliation artifact produced by the <see cref="GeoservicesImportStatus.Validating"/>
    /// phase, when reconciliation ran. <c>null</c> until the Validating phase completes (or when the
    /// import did not publish a queryable layer to reconcile against).
    /// </summary>
    public MigrationReconciliationArtifact? ReconciliationArtifact { get; init; }

    /// <summary>
    /// Catalog parity report produced by the <see cref="GeoservicesImportStatus.Validating"/> phase,
    /// when a published Metadata v2 entry was available to reconcile against. <c>null</c> otherwise.
    /// </summary>
    public MigrationCatalogReconciliationReport? CatalogReconciliationReport { get; init; }

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

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = GeoservicesImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new import job.
    /// </summary>
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

/// <summary>
/// Status of a Geoservices import operation.
/// </summary>
public enum GeoservicesImportStatus
{
    /// <summary>
    /// Import is queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Discovering service metadata.
    /// </summary>
    Discovering,

    /// <summary>
    /// Retrieving features from remote service.
    /// </summary>
    RetrievingFeatures,

    /// <summary>
    /// Creating PostGIS table.
    /// </summary>
    CreatingTable,

    /// <summary>
    /// Inserting features into PostGIS.
    /// </summary>
    InsertingFeatures,

    /// <summary>
    /// Publishing the imported layer.
    /// </summary>
    Publishing,

    /// <summary>
    /// Copying source feature attachments into the Honua attachment store.
    /// </summary>
    CopyingAttachments,

    /// <summary>
    /// Post-publish reconciliation is comparing the published layer against the source snapshot.
    /// </summary>
    Validating,

    /// <summary>
    /// Import completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// The import published data but a hard reconciliation finding (count/geometry/content/extent
    /// or catalog parity) routed the run to operator review before it can be declared complete. The
    /// imported data and layer are left in place; an operator audits the discrepancy and decides
    /// whether to accept, re-import, or roll back.
    /// </summary>
    NeedsReview,

    /// <summary>
    /// Import failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Import was cancelled.
    /// </summary>
    Cancelled
}
