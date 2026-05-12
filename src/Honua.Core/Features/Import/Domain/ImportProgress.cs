// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Import.Domain;

/// <summary>
/// Status of an import operation.
/// </summary>
public enum ImportStatus
{
    /// <summary>
    /// Import is queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Import is currently being processed.
    /// </summary>
    Processing,

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

/// <inheritdoc/>
public sealed record ImportProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this import job.
    /// </summary>
    public required string JobId { get; init; }

    /// <summary>
    /// Current status of the import.
    /// </summary>
    public required ImportStatus Status { get; init; }

    /// <summary>
    /// Number of features processed so far.
    /// </summary>
    public int FeaturesProcessed { get; init; }

    /// <summary>
    /// Estimated total number of features (may be unknown for streaming formats).
    /// </summary>
    public int? EstimatedTotalFeatures { get; init; }

    /// <summary>
    /// Number of batches committed to database.
    /// </summary>
    public int BatchesCommitted { get; init; }

    /// <summary>
    /// Number of features that failed to import.
    /// </summary>
    public int FailedFeatures { get; init; }

    /// <summary>
    /// Number of bytes read from the source stream.
    /// </summary>
    public long BytesRead { get; init; }

    /// <summary>
    /// Total size of the source file (if known).
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete => TotalBytes > 0 ? (double)BytesRead / TotalBytes * 100 : null;

    /// <summary>
    /// Table name being imported to.
    /// </summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Original file name being imported.
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Stable source kind for admin clients that render mixed import job results.
    /// </summary>
    public string SourceKind { get; init; } = "file";

    /// <summary>
    /// Source URL for URL-based imports.
    /// </summary>
    public string? SourceUrl { get; init; }

    /// <summary>
    /// Cloud storage file ID when the import source was staged to object storage.
    /// </summary>
    public string? CloudFileId { get; init; }

    /// <summary>
    /// Upload operation ID associated with this import job, when supplied by the client or storage provider.
    /// </summary>
    public string? UploadId { get; init; }

    /// <summary>
    /// Detected source SRID from the completed import result, when known.
    /// </summary>
    public int? DetectedSrid { get; init; }

    /// <summary>
    /// Target Honua service name when a future file publish flow associates this import with a service.
    /// </summary>
    public string? ServiceName { get; init; }

    /// <summary>
    /// Published Honua layer ID when a future file publish flow creates a layer.
    /// </summary>
    public int? PublishedLayerId { get; init; }

    /// <summary>
    /// Published layer ID alias used by generic admin import result views.
    /// </summary>
    public int? LayerId => PublishedLayerId;

    /// <summary>
    /// Detected or specified file format.
    /// </summary>
    public required SupportedFileFormat Format { get; init; }

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
    OperationType IOperationProgress.Type => OperationType.Import;
    OperationStatus IOperationProgress.Status => Status switch
    {
        ImportStatus.Queued => OperationStatus.Queued,
        ImportStatus.Processing => OperationStatus.Processing,
        ImportStatus.Completed => OperationStatus.Completed,
        ImportStatus.Failed => OperationStatus.Failed,
        ImportStatus.Cancelled => OperationStatus.Cancelled,
        _ => OperationStatus.Queued
    };

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = ImportStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new import job.
    /// </summary>
    public static ImportProgress CreateInitial(
        string jobId,
        string tableName,
        SupportedFileFormat format,
        long? totalBytes = null,
        string? fileName = null,
        string? sourceKind = null,
        string? sourceUrl = null,
        string? cloudFileId = null,
        string? uploadId = null,
        IReadOnlyList<string>? warnings = null)
        => new()
        {
            JobId = jobId,
            Status = ImportStatus.Queued,
            TableName = tableName,
            FileName = fileName,
            SourceKind = string.IsNullOrWhiteSpace(sourceKind) ? "file" : sourceKind,
            SourceUrl = sourceUrl,
            CloudFileId = cloudFileId,
            UploadId = uploadId,
            Format = format,
            TotalBytes = totalBytes,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for processing",
            Warnings = warnings ?? []
        };
}

/// <summary>
/// Progress information for data ingest operations (processing uploaded data).
/// </summary>
public sealed record IngestProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this ingest operation.
    /// </summary>
    public required string IngestId { get; init; }

    /// <summary>
    /// Current status of the ingest.
    /// </summary>
    public required OperationStatus Status { get; init; }

    /// <summary>
    /// Number of records processed so far.
    /// </summary>
    public long RecordsProcessed { get; init; }

    /// <summary>
    /// Estimated total number of records (may be unknown).
    /// </summary>
    public long? EstimatedTotalRecords { get; init; }

    /// <summary>
    /// Number of records that failed to process.
    /// </summary>
    public long FailedRecords { get; init; }

    /// <summary>
    /// Number of bytes processed from the source.
    /// </summary>
    public long BytesProcessed { get; init; }

    /// <summary>
    /// Total size of the source data.
    /// </summary>
    public long? TotalBytes { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    public double? PercentComplete => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100 : null;

    /// <summary>
    /// Source data identifier (e.g., file ID, URL).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Target destination (e.g., table name, collection name).
    /// </summary>
    public required string TargetDestination { get; init; }

    /// <summary>
    /// Type of ingest operation.
    /// </summary>
    public required string IngestType { get; init; }

    /// <summary>
    /// When the ingest started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the ingest completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the ingest operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the ingest failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// List of warning messages encountered during ingest.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => IngestId;
    OperationType IOperationProgress.Type => OperationType.Ingest;

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new ingest operation.
    /// </summary>
    public static IngestProgress CreateInitial(string ingestId, string sourceId, string targetDestination, string ingestType, long? totalBytes = null)
        => new()
        {
            IngestId = ingestId,
            Status = OperationStatus.Queued,
            SourceId = sourceId,
            TargetDestination = targetDestination,
            IngestType = ingestType,
            TotalBytes = totalBytes,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for ingest"
        };
}
