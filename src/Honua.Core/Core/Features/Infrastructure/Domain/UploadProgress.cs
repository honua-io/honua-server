// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public sealed record UploadProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string UploadId { get; init; }

    public required OperationStatus Status { get; init; }

    public long BytesUploaded { get; init; }

    public long TotalBytes { get; init; }

    public double? PercentComplete => TotalBytes > 0 ? (double)BytesUploaded / TotalBytes * 100 : null;

    public required string FileName { get; init; }

    public string? ContentType { get; init; }

    public string? CloudFileId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => UploadId;
    OperationType IOperationProgress.Type => OperationType.Upload;

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

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
