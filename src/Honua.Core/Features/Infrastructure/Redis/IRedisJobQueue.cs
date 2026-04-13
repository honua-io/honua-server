// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Redis;

/// <summary>
/// Interface for Redis-backed job processing with fallback capabilities.
/// </summary>
/// <typeparam name="T">The type of jobs handled by this processor</typeparam>
public interface IRedisJobProcessor<T> : IRedisService
{
    /// <summary>
    /// Gets the queue key used by this job processor.
    /// </summary>
    string QueueKey { get; }

    /// <summary>
    /// Enqueues a job for processing.
    /// </summary>
    /// <param name="job">The job to enqueue</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    Task EnqueueAsync(T job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Dequeues a job for processing.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The dequeued job, or null if no jobs are available</returns>
    Task<T?> DequeueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current queue length.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The number of jobs in the queue</returns>
    Task<long> GetQueueLengthAsync(CancellationToken cancellationToken = default);
}