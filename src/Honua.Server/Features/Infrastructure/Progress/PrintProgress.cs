// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Infrastructure.Progress;

/// <summary>
/// Progress entry for asynchronous print/map composition operations.
/// </summary>
internal sealed record PrintProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }
    public required string Format { get; init; }
    public required string TemplateName { get; init; }
    public required OperationStatus Status { get; init; }
    public int TotalElements { get; init; }
    public int ProcessedElements { get; init; }
    public long OutputSizeBytes { get; init; }
    public string? DownloadUrl { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? CurrentPhase { get; init; }

    // Print rendering is atomic (single ExecuteAsync call), so ProcessedElements
    // jumps from 0 to TotalElements on completion.  Return null (indeterminate)
    // while ProcessedElements is still 0; CurrentPhase provides the status signal.
    public double? PercentComplete => ProcessedElements > 0 && TotalElements > 0
        ? Math.Clamp((double)ProcessedElements / TotalElements * 100.0, 0.0, 100.0)
        : null;

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => OperationType.Print;

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase ?? "Cancelled"
        };

    public static PrintProgress CreateInitial(
        string jobId,
        string format,
        string templateName,
        int totalElements)
        => new()
        {
            JobId = jobId,
            Format = format,
            TemplateName = templateName,
            TotalElements = totalElements,
            Status = OperationStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}
