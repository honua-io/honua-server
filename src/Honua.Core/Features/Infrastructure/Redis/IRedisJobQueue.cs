// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

/// <summary>
/// Interface for Redis-backed job queue with fallback capabilities.
/// </summary>
public interface IRedisJobQueue : IRedisService
{
    /// <summary>
    /// Gets the queue key used by this job queue.
    /// </summary>
    string QueueKey { get; }

    /// <summary>
    /// Enqueues a job for processing.
    /// </summary>
    /// <param name="job">The job to enqueue (JSON serializable)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task EnqueueAsync(string job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeues a job for processing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The dequeued job as JSON string, or null if no jobs are available</returns>
    Task<string?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current queue length.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of jobs in the queue</returns>
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the number of jobs being processed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of jobs currently being processed</returns>
    Task<long> GetProcessingCountAsync(CancellationToken cancellationToken = default);
}