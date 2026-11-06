// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

public interface IOperationProgress
{
    string OperationId { get; }

    OperationType Type { get; }

    OperationStatus Status { get; }

    double? PercentComplete { get; }

    DateTimeOffset StartedAt { get; }

    DateTimeOffset? CompletedAt { get; }

    TimeSpan Duration { get; }

    string? ErrorMessage { get; }

    IReadOnlyList<string> Warnings { get; }

    string? CurrentPhase { get; }
}

public enum OperationStatus
{
    Queued,

    Processing,

    Completed,

    Failed,

    Cancelled
}

public enum OperationType
{
    Upload,

    Import,

    Ingest,

    ExternalImport,

    TileCache,

    PMTilesArchive,

    Export,

    RasterImport,

    Print,

    Geoprocessing,

    Publishing,

    Orchestration,

    Deployment,

    PMTilesPublish,

    TemporalCorrective
}
