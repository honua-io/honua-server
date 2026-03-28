// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Domain;

/// <summary>
/// Common interface for all operation progress tracking.
/// Provides a unified progress interface across upload, import, and ingest operations.
/// </summary>
public interface IOperationProgress
{
    /// <summary>
    /// Unique identifier for this operation.
    /// </summary>
    string OperationId { get; }

    /// <summary>
    /// Type of operation being tracked.
    /// </summary>
    OperationType Type { get; }

    /// <summary>
    /// Current status of the operation.
    /// </summary>
    OperationStatus Status { get; }

    /// <summary>
    /// Progress percentage (0-100), null if total is unknown.
    /// </summary>
    double? PercentComplete { get; }

    /// <summary>
    /// When the operation started.
    /// </summary>
    DateTimeOffset StartedAt { get; }

    /// <summary>
    /// When the operation completed (null if still running).
    /// </summary>
    DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// Duration of the operation.
    /// </summary>
    TimeSpan Duration { get; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    string? ErrorMessage { get; }

    /// <summary>
    /// List of warning messages encountered during operation.
    /// </summary>
    IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Current processing phase description.
    /// </summary>
    string? CurrentPhase { get; }
}

/// <summary>
/// Unified operation status for all operation types.
/// </summary>
public enum OperationStatus
{
    /// <summary>
    /// Operation is queued for processing.
    /// </summary>
    Queued,

    /// <summary>
    /// Operation is currently being processed.
    /// </summary>
    Processing,

    /// <summary>
    /// Operation completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Operation failed with errors.
    /// </summary>
    Failed,

    /// <summary>
    /// Operation was cancelled.
    /// </summary>
    Cancelled
}

/// <summary>
/// Type of operation being tracked.
/// </summary>
public enum OperationType
{
    /// <summary>
    /// File upload operation.
    /// </summary>
    Upload,

    /// <summary>
    /// File import operation (file to database).
    /// </summary>
    Import,

    /// <summary>
    /// Data ingest operation (processing uploaded data).
    /// </summary>
    Ingest,

    /// <summary>
    /// External service import (e.g., ArcGIS to database).
    /// </summary>
    ExternalImport,

    /// <summary>
    /// Tile cache lifecycle operation (seed/warm/invalidate/purge).
    /// </summary>
    TileCache,

    /// <summary>
    /// PMTiles archive generation operation.
    /// </summary>
    PMTilesArchive,

    /// <summary>
    /// Data export operation (Shapefile, GeoPackage, CSV).
    /// </summary>
    Export,

    /// <summary>
    /// Print/map composition operation (PDF, PNG, JPG).
    /// </summary>
    Print,

    /// <summary>
    /// Raster file import operation (GeoTIFF/world-file to PostGIS raster).
    /// </summary>
    RasterImport
}
