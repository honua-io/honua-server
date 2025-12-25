// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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

/// <summary>
/// Progress information for a running or completed import operation.
/// </summary>
public sealed record ImportProgress
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
    /// Create an initial progress record for a new import job.
    /// </summary>
    public static ImportProgress CreateInitial(string jobId, string tableName, SupportedFileFormat format, long? totalBytes = null)
        => new()
        {
            JobId = jobId,
            Status = ImportStatus.Queued,
            TableName = tableName,
            Format = format,
            TotalBytes = totalBytes,
            StartedAt = DateTimeOffset.UtcNow
        };
}
