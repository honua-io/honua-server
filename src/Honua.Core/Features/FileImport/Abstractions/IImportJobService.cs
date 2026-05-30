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

namespace Honua.Core.Features.FileImport.Abstractions;

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
