// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Distributed progress store for tracking long-running operations.
/// </summary>
public interface IDistributedProgressStore<TProgress> where TProgress : class
{
    /// <summary>
    /// Store progress for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="progress">Progress data</param>
    /// <param name="ttl">Time-to-live for the progress data</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task SetProgressAsync(string jobId, TProgress progress, TimeSpan? ttl = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieve progress for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Progress data or null if not found</returns>
    Task<TProgress?> GetProgressAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete progress data for a job.
    /// </summary>
    /// <param name="jobId">Job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteProgressAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get all active job IDs (jobs with progress data).
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task<IReadOnlyList<string>> GetActiveJobIdsAsync(CancellationToken cancellationToken = default);
}
