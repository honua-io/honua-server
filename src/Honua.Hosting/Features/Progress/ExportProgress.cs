// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Infrastructure.Progress;

/// <summary>
/// Progress entry for asynchronous data export operations.
/// </summary>
internal sealed record ExportProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }
    public required string Format { get; init; }
    public required string ServiceName { get; init; }
    public required int LayerId { get; init; }
    public required OperationStatus Status { get; init; }
    public long TotalFeatures { get; init; }
    public long ProcessedFeatures { get; init; }
    public long OutputSizeBytes { get; init; }
    public string? DownloadUrl { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? CurrentPhase { get; init; }

    public double? PercentComplete => TotalFeatures > 0
        ? Math.Clamp((double)ProcessedFeatures / TotalFeatures * 100.0, 0.0, 100.0)
        : null;

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.Export;

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase ?? "Cancelled"
        };

    public static ExportProgress CreateInitial(
        string jobId,
        string format,
        string serviceName,
        int layerId,
        long totalFeatures)
        => new()
        {
            JobId = jobId,
            Format = format,
            ServiceName = serviceName,
            LayerId = layerId,
            TotalFeatures = totalFeatures,
            Status = OperationStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}
