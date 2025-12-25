// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Service for importing geospatial files into PostgreSQL/PostGIS
/// </summary>
public interface IFileImportService
{
    /// <summary>
    /// Detect file format from filename extension
    /// </summary>
    /// <param name="fileName">Original filename</param>
    /// <returns>Detected file format or null if not supported</returns>
    SupportedFileFormat? DetectFormat(string fileName);

    /// <summary>
    /// Get supported file extensions
    /// </summary>
    /// <returns>Array of supported extensions (with dots)</returns>
    string[] GetSupportedExtensions();

    /// <summary>
    /// Import a geospatial file into PostgreSQL using memory-efficient streaming.
    /// Features are processed in batches to maintain constant memory usage.
    /// </summary>
    /// <param name="request">Import request with file stream and parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<ImportResult> ImportFileAsync(ImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a geospatial file with progress reporting.
    /// </summary>
    /// <param name="request">Import request with file stream and parameters</param>
    /// <param name="progress">Progress reporter for tracking import status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<ImportResult> ImportFileAsync(ImportRequest request, IProgress<ImportProgress>? progress, CancellationToken cancellationToken = default);

    /// <summary>
    /// Preview file contents without importing (first N features based on limits).
    /// Uses streaming to avoid loading entire file into memory.
    /// </summary>
    /// <param name="fileStream">File stream to preview</param>
    /// <param name="fileName">Original filename for format detection</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Preview information including feature count and sample features</returns>
    Task<FilePreview> PreviewFileAsync(Stream fileStream, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the configured import limits.
    /// </summary>
    ImportLimits Limits { get; }
}

/// <summary>
/// Service for managing background import jobs for large files.
/// </summary>
public interface IImportJobService
{
    /// <summary>
    /// Queue a file for background import processing.
    /// </summary>
    /// <param name="request">Import request with file stream and parameters</param>
    /// <param name="fileSize">Size of the file in bytes</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job ID for tracking progress</returns>
    Task<string> QueueImportAsync(ImportRequest request, long fileSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current progress of an import job.
    /// </summary>
    /// <param name="jobId">Job ID returned from QueueImportAsync</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Current progress or null if job not found</returns>
    Task<ImportProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancel a running import job.
    /// </summary>
    /// <param name="jobId">Job ID to cancel</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if job was cancelled, false if not found or already completed</returns>
    Task<bool> CancelJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active import jobs.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active job progress records</returns>
    Task<IReadOnlyList<ImportProgress>> GetActiveJobsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Service for detecting coordinate reference systems from various sources
/// </summary>
public interface ICrsDetectionService
{
    /// <summary>
    /// Detect CRS from a .prj file content (shapefile projection file format)
    /// </summary>
    /// <param name="prjContent">Content of a .prj file</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromPrjAsync(string prjContent);

    /// <summary>
    /// Detect CRS from Well-Known Text string
    /// </summary>
    /// <param name="wktContent">WKT string representation</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromWktAsync(string wktContent);

    /// <summary>
    /// Detect CRS from EPSG code
    /// </summary>
    /// <param name="epsgCode">EPSG code (e.g., "EPSG:4326" or "4326")</param>
    /// <returns>Parsed SRID or null if invalid</returns>
    int? DetectFromEpsgCode(string epsgCode);

    /// <summary>
    /// Detect CRS from GeoJSON CRS object
    /// </summary>
    /// <param name="crsObject">GeoJSON CRS object as JSON</param>
    /// <returns>Detected SRID or null if not recognized</returns>
    Task<int?> DetectFromGeoJsonCrsAsync(string crsObject);

    /// <summary>
    /// Detect CRS from shapefile by looking for accompanying .prj file
    /// </summary>
    /// <param name="shapefilePath">Path to the .shp file</param>
    /// <returns>Detected SRID or null if no .prj file found or not recognized</returns>
    Task<int?> DetectFromShapefilePrjAsync(string shapefilePath);

    /// <summary>
    /// Validate that an SRID exists in the spatial reference system database
    /// </summary>
    /// <param name="srid">SRID to validate</param>
    /// <returns>True if SRID exists in the database</returns>
    Task<bool> ValidateSridAsync(int srid);
}

/// <summary>
/// Preview information for a geospatial file
/// </summary>
public sealed record FilePreview
{
    /// <summary>
    /// Detected file format
    /// </summary>
    public required SupportedFileFormat Format { get; init; }

    /// <summary>
    /// Total number of features in the file
    /// </summary>
    public int TotalFeatureCount { get; init; }

    /// <summary>
    /// Detected coordinate reference system
    /// </summary>
    public int? DetectedSrid { get; init; }

    /// <summary>
    /// Sample feature properties (first feature)
    /// </summary>
    public Dictionary<string, object?> SampleProperties { get; init; } = new();

    /// <summary>
    /// Available layers (for multi-layer formats like GeoPackage)
    /// </summary>
    public string[] AvailableLayers { get; init; } = Array.Empty<string>();
}
