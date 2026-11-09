// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Domain;

public enum RasterImportPhase
{
    Queued,

    Validating,

    Ingesting,

    ComputingStatistics,

    GeneratingTiles,

    Completed,

    Failed
}

public sealed record RasterImportProgress : IOperationProgress
{
    public required string OperationId { get; init; }

    public required RasterImportPhase Phase { get; init; }

    public required OperationStatus Status { get; init; }

    public long? RasterId { get; init; }

    public int BandsProcessed { get; init; }

    public int? TotalBands { get; init; }

    public int TilesGenerated { get; init; }

    public int? TotalTiles { get; init; }

    public double? PercentComplete { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? CompletedAt { get; init; }

    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => OperationId;
    OperationType IOperationProgress.Type => OperationType.RasterImport;

    public static RasterImportProgress CreateInitial(string operationId)
        => new()
        {
            OperationId = operationId,
            Phase = RasterImportPhase.Queued,
            Status = OperationStatus.Queued,
            StartedAt = DateTimeOffset.UtcNow,
            CurrentPhase = "Queued for processing"
        };
}
