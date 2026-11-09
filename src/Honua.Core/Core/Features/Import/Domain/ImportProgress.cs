// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Import.Domain;

public enum ImportStatus
{
    Queued,

    Processing,

    Completed,

    Failed,

    Cancelled
}

public sealed record ImportProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }

    public string? OperationInstanceId { get; init; }

    public string? CorrelationId { get; init; }

    public string? AuditId { get; init; }

    public string? ProposalId { get; init; }

    public required ImportStatus Status { get; init; }

    public int FeaturesProcessed { get; init; }

    public int? EstimatedTotalFeatures { get; init; }

    public int BatchesCommitted { get; init; }

    public int FailedFeatures { get; init; }

    public int RepairedFeatures { get; init; }

    public long BytesRead { get; init; }

    public long? TotalBytes { get; init; }

    public double? PercentComplete => TotalBytes > 0 ? (double)BytesRead / TotalBytes * 100 : null;

    public required string TableName { get; init; }

    public string? PhysicalTableName { get; init; }

    public string? Schema { get; init; }

    public string? FileName { get; init; }

    public string SourceKind { get; init; } = "file";

    public string? SourceUrl { get; init; }

    public string? CloudFileId { get; init; }

    public string? UploadId { get; init; }

    public int? DetectedSrid { get; init; }

    public string? ServiceName { get; init; }

    public int? PublishedLayerId { get; init; }

    public int? LayerId => PublishedLayerId;

    public required SupportedFileFormat Format { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public string? ErrorCode { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    public string? Runner { get; init; }

    public string? InstanceId { get; init; }

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

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = ImportStatus.Cancelled,
            PhysicalTableName = null,
            Schema = null,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

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
        IReadOnlyList<string>? warnings = null,
        string? operationInstanceId = null,
        string? correlationId = null,
        string? auditId = null,
        string? proposalId = null)
        => new()
        {
            JobId = jobId,
            OperationInstanceId = operationInstanceId,
            CorrelationId = correlationId,
            AuditId = auditId,
            ProposalId = proposalId,
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

public sealed record IngestProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string IngestId { get; init; }

    public required OperationStatus Status { get; init; }

    public long RecordsProcessed { get; init; }

    public long? EstimatedTotalRecords { get; init; }

    public long FailedRecords { get; init; }

    public long BytesProcessed { get; init; }

    public long? TotalBytes { get; init; }

    public double? PercentComplete => TotalBytes > 0 ? (double)BytesProcessed / TotalBytes * 100 : null;

    public required string SourceId { get; init; }

    public required string TargetDestination { get; init; }

    public required string IngestType { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => IngestId;
    OperationType IOperationProgress.Type => OperationType.Ingest;

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase
        };

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
