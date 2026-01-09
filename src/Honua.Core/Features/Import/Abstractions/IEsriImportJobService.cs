// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Service for managing background Esri import jobs.
/// Designed to support distributed execution with Redis leader election.
/// </summary>
public interface IEsriImportJobService
{
    /// <summary>
    /// Queue a layer import for background processing.
    /// </summary>
    /// <param name="request">Import request with layer details</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job ID for tracking progress</returns>
    Task<string> QueueImportAsync(
        EsriImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current progress of an import job.
    /// </summary>
    /// <param name="jobId">Job ID returned from QueueImportAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current progress or null if job not found</returns>
    Task<EsriImportProgress?> GetProgressAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running import job.
    /// </summary>
    /// <param name="jobId">Job ID to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if job was cancelled, false if not found or already completed</returns>
    Task<bool> CancelJobAsync(
        string jobId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active import jobs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active job progress records</returns>
    Task<IReadOnlyList<EsriImportProgress>> GetActiveJobsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Try to acquire leadership for processing jobs.
    /// Used for distributed coordination with Redis.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if this instance is the leader</returns>
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Release leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if this instance currently holds leadership.
    /// </summary>
    bool IsLeader { get; }
}

/// <summary>
/// Progress information for an Esri import operation.
/// </summary>
public sealed record EsriImportProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this import job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the import.
    /// </summary>
    public required EsriImportStatus Status { get; init; }

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

    // IOperationProgress implementation
    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.ExternalImport;
    OperationStatus IOperationProgress.Status => Status switch
    {
        EsriImportStatus.Queued => OperationStatus.Queued,
        EsriImportStatus.Discovering => OperationStatus.Processing,
        EsriImportStatus.RetrievingFeatures => OperationStatus.Processing,
        EsriImportStatus.CreatingTable => OperationStatus.Processing,
        EsriImportStatus.InsertingFeatures => OperationStatus.Processing,
        EsriImportStatus.Publishing => OperationStatus.Processing,
        EsriImportStatus.Completed => OperationStatus.Completed,
        EsriImportStatus.Failed => OperationStatus.Failed,
        EsriImportStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = EsriImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new import job.
    /// </summary>
    public static EsriImportProgress CreateInitial(
        string jobId,
        string sourceServiceUrl,
        int sourceLayerId,
        string tableName,
        string? sourceLayerName = null,
        int? estimatedTotalFeatures = null) =>
        new()
        {
            JobId = jobId,
            Status = EsriImportStatus.Queued,
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
/// Status of an Esri import operation.
/// </summary>
public enum EsriImportStatus
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
