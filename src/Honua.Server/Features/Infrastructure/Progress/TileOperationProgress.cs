// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;

namespace Honua.Server.Features.Infrastructure.Progress;

/// <summary>
/// Progress entry for asynchronous tile lifecycle operations.
/// </summary>
internal sealed record TileOperationProgress : IOperationProgress, ICancellableOperationProgress
{
    public required string JobId { get; init; }
    public required string Operation { get; init; }
    public string? ServiceId { get; init; }
    public int? LayerId { get; init; }
    public string? TileMatrixSetId { get; init; }
    public required OperationStatus Status { get; init; }
    public long TotalTiles { get; init; }
    public long ProcessedTiles { get; init; }
    public long SuccessfulTiles { get; init; }
    public long FailedTiles { get; init; }
    public long ArchiveSizeBytes { get; init; }
    public string? ArchiveFileId { get; init; }
    public string? DownloadUrl { get; init; }
    public PMTilesArtifactDescriptor? PublishedArtifact { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public string? CurrentPhase { get; init; }

    public double? PercentComplete => TotalTiles > 0
        ? Math.Clamp((double)ProcessedTiles / TotalTiles * 100.0, 0.0, 100.0)
        : null;

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    string IOperationProgress.OperationId => JobId;
    OperationType IOperationProgress.Type => Operation switch
    {
        "archive" => OperationType.PMTilesArchive,
        "publish" => OperationType.PMTilesPublish,
        _ => OperationType.TileCache
    };

    public IOperationProgress WithCancellation(DateTimeOffset completedAt, string? currentPhase)
        => this with
        {
            Status = OperationStatus.Cancelled,
            CompletedAt = completedAt,
            CurrentPhase = currentPhase ?? "Cancelled"
        };

    public static TileOperationProgress CreateInitial(
        string jobId,
        string operation,
        string? serviceId,
        int? layerId,
        string? tileMatrixSetId)
        => new()
        {
            JobId = jobId,
            Operation = operation,
            ServiceId = serviceId,
            LayerId = layerId,
            TileMatrixSetId = tileMatrixSetId,
            Status = OperationStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued"
        };
}
