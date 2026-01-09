// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Store for tracking upload progress across distributed instances.
/// </summary>
public interface IUploadProgressStore
{
    /// <summary>
    /// Store progress for an upload operation.
    /// </summary>
    /// <param name="uploadId">Unique upload identifier</param>
    /// <param name="progress">Progress data</param>
    /// <param name="ttl">Time-to-live for the progress data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetProgressAsync(string uploadId, UploadProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve progress for an upload operation.
    /// </summary>
    /// <param name="uploadId">Unique upload identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Progress data or null if not found</returns>
    Task<UploadProgress?> GetProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete progress data for an upload operation.
    /// </summary>
    /// <param name="uploadId">Unique upload identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteProgressAsync(string uploadId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active upload IDs (uploads with progress data).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active upload identifiers</returns>
    Task<IReadOnlyList<string>> GetActiveUploadIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active upload progress records.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>List of active upload progress records</returns>
    Task<IReadOnlyList<UploadProgress>> GetActiveUploadsAsync(CancellationToken cancellationToken = default);
}
