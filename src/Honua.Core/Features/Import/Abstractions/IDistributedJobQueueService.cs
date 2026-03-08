// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Distributed job queue abstraction for background processing.
/// </summary>
public interface IDistributedJobQueueService
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
    /// Marks a previously dequeued job as durably completed so it is removed from in-flight storage.
    /// </summary>
    /// <param name="jobId">Unique job identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task CompleteAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requeues any in-flight jobs left behind by a previous worker crash or restart.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task RecoverInFlightAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the current queue length.
    /// </summary>
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
}
