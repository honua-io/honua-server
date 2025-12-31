// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Distributed job queue abstraction for background processing.
/// </summary>
public interface IDistributedJobQueue
{
    /// <summary>
    /// Enqueue a job for background processing.
    /// </summary>
    /// <param name="jobId">Unique job identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task EnqueueAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeue the next job. Returns null if no job available within timeout.
    /// </summary>
    /// <param name="timeout">How long to wait for a job</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Job ID or null if timeout</returns>
    Task<string?> DequeueAsync(TimeSpan timeout, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current queue length.
    /// </summary>
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Distributed leader election for singleton background processing.
/// </summary>
public interface IDistributedLeaderElection
{
    /// <summary>
    /// Try to acquire leadership. Only one instance can be leader at a time.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if this instance is now the leader</returns>
    Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Extend the leadership lease. Must be called periodically to maintain leadership.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if lease was extended, false if leadership was lost</returns>
    Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Release leadership voluntarily.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if this instance currently holds leadership.
    /// </summary>
    bool IsLeader { get; }

    /// <summary>
    /// Unique identifier for this instance.
    /// </summary>
    string InstanceId { get; }
}

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

/// <summary>
/// Combined interface for distributed import job management.
/// </summary>
public interface IDistributedImportJobManager
{
    /// <summary>
    /// Job queue for Esri imports.
    /// </summary>
    IDistributedJobQueue JobQueue { get; }

    /// <summary>
    /// Leader election for background processing.
    /// </summary>
    IDistributedLeaderElection LeaderElection { get; }

    /// <summary>
    /// Progress store for tracking import jobs.
    /// </summary>
    IDistributedProgressStore<EsriImportProgress> ProgressStore { get; }

    /// <summary>
    /// Store for import requests (needed by background worker).
    /// </summary>
    IDistributedProgressStore<EsriImportRequest> RequestStore { get; }
}
