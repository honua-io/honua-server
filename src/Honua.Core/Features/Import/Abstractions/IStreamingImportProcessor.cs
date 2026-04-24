// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Service responsible for the core streaming import processing logic.
/// Segregated interface following the Single Responsibility and Interface Segregation principles.
/// </summary>
public interface IStreamingImportProcessor
{
    /// <summary>
    /// Import a geospatial file into PostgreSQL using memory-efficient streaming.
    /// Features are processed in batches to maintain constant memory usage.
    /// </summary>
    /// <param name="request">Import request with file stream and parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<ImportResult> ProcessImportAsync(
        ImportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Import a geospatial file with progress reporting.
    /// </summary>
    /// <param name="request">Import request with file stream and parameters</param>
    /// <param name="progress">Progress reporter for tracking import status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Import result with success/failure details</returns>
    Task<ImportResult> ProcessImportAsync(
        ImportRequest request,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the configured import limits.
    /// </summary>
    ImportLimits Limits { get; }
}
