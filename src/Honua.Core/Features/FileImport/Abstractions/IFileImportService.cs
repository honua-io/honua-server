// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;
using Honua.Core.Features.FileImport.Services.FileGdb;

namespace Honua.Core.Features.FileImport.Abstractions;

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

    /// <summary>
    /// Warnings about advanced constructs or unsupported features detected during preview
    /// </summary>
    public string[] Warnings { get; init; } = Array.Empty<string>();
}
