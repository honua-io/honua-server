// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Raster.Domain;

/// <summary>
/// Import phase for raster ingestion.
/// </summary>
public enum RasterImportPhase
{
    /// <summary>Queued for processing.</summary>
    Queued,

    /// <summary>Validating the raster file.</summary>
    Validating,

    /// <summary>Loading raster data into PostGIS.</summary>
    Ingesting,

    /// <summary>Computing band statistics.</summary>
    ComputingStatistics,

    /// <summary>Pre-generating tiles at configured zoom levels.</summary>
    GeneratingTiles,

    /// <summary>Import completed.</summary>
    Completed,

    /// <summary>Import failed.</summary>
    Failed
}

/// <summary>
/// Progress information for a raster import operation.
/// Not cancellable via admin API — the import runs synchronously within the HTTP request
/// and is cancelled via request disconnection or timeout. Admin cancellation can be added
/// if the import is made asynchronous in a future pass.
/// </summary>
public sealed record RasterImportProgress : IOperationProgress
{
    /// <summary>
    /// Unique identifier for this import operation.
    /// </summary>
    public required string OperationId { get; init; }

    /// <summary>
    /// Current import phase.
    /// </summary>
    public required RasterImportPhase Phase { get; init; }

    /// <summary>
    /// Current status of the operation.
    /// </summary>
    public required OperationStatus Status { get; init; }

    /// <summary>
    /// Raster data ID once ingested (null until ingest completes).
    /// </summary>
    public long? RasterId { get; init; }

    /// <summary>
    /// Number of bands in the raster.
    /// </summary>
    public int BandsProcessed { get; init; }

    /// <summary>
    /// Total number of bands to process.
    /// </summary>
    public int? TotalBands { get; init; }

    /// <summary>
    /// Number of tiles generated so far.
    /// </summary>
    public int TilesGenerated { get; init; }

    /// <summary>
    /// Total tiles to generate (null if not yet computed).
    /// </summary>
    public int? TotalTiles { get; init; }

    /// <summary>
    /// Progress percentage (0-100), null if unknown.
    /// </summary>
    public double? PercentComplete { get; init; }

    /// <summary>
    /// When the import started.
    /// </summary>
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// When the import completed (null if still running).
    /// </summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    public TimeSpan Duration => (CompletedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    /// <summary>
    /// Error message if the import failed.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Warning messages encountered during import.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    public string? CurrentPhase { get; init; }

    // IOperationProgress implementation
    string IOperationProgress.OperationId => OperationId;
    OperationType IOperationProgress.Type => OperationType.RasterImport;

    /// <summary>
    /// Create an initial progress record for a new raster import.
    /// </summary>
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
