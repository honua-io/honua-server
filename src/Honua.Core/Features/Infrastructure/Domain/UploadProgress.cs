// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Progress information for file upload operations.
/// </summary>
public sealed record UploadProgress : IOperationProgress, ICancellableOperationProgress
{
    /// <summary>
    /// Unique identifier for this upload operation.
    /// </summary>
    public required string UploadId { get; init; }

    /// <summary>
    /// Current status of the upload.
    /// </summary>
    public required OperationStatus Status { get; init; }

    /// <summary>
    /// Number of bytes uploaded so far.
    /// </summary>
    public long BytesUploaded { get; init; }

    /// <summary>
    /// Total size of the file being uploaded.
    /// </summary>
    public long TotalBytes { get; init; }

    /// <summary>
    /// Progress percentage (0-100).
    /// </summary>
    public double? PercentComplete => TotalBytes > 0 ? (double)BytesUploaded / TotalBytes * 100 : null;

    /// <summary>
    /// Original file name.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// Content type of the uploaded file.
    /// </summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Cloud storage file ID (if applicable).
    /// </summary>
    public string? CloudFileId { get; init; }

    /// <summary>
    /// When the upload started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the upload completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the upload operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the upload failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// List of warning messages encountered during upload.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => UploadId;
    OperationType IOperationProgress.Type => OperationType.Upload;

    /// <inheritdoc />
    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

    /// <summary>
    /// Create an initial progress record for a new upload operation.
    /// </summary>
    public static UploadProgress CreateInitial(string uploadId, string fileName, long totalBytes, string? contentType = null)
        => new()
        {
            UploadId = uploadId,
            Status = OperationStatus.Queued,
            FileName = fileName,
            ContentType = contentType,
            TotalBytes = totalBytes,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for upload"
        };
}
