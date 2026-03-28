// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Raster.Domain;

namespace Honua.Core.Features.Raster.Abstractions;

/// <summary>
/// Service for importing raster files into PostGIS.
/// Separated from <see cref="IRasterStore"/> to keep the read/query contract clean.
/// </summary>
public interface IRasterImportService
{
    /// <summary>
    /// Import a raster file into the specified layer.
    /// Loads the file into raster_data, computes statistics, and pre-generates tiles.
    /// </summary>
    /// <param name="request">Import request with file path and parameters</param>
    /// <param name="progress">Optional progress reporter for tracking import phases</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<RasterImportResult> ImportAsync(
        RasterImportRequest request,
        IProgress<RasterImportProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Detect raster format from filename extension.
    /// </summary>
    /// <param name="fileName">Original filename</param>
    /// <returns>Detected raster format or null if not supported</returns>
    SupportedRasterFormat? DetectFormat(string fileName);

    /// <summary>
    /// Get supported raster file extensions.
    /// </summary>
    /// <returns>Array of supported extensions (with dots)</returns>
    string[] GetSupportedExtensions();
}
