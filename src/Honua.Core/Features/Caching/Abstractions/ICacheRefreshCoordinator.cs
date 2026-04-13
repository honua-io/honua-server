// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

/// <summary>
/// Coordinates background cache refresh operations with per-key deduplication.
/// </summary>
public interface ICacheRefreshCoordinator
{
    /// <summary>
    /// The current number of pending refresh operations (queued + in-flight).
    /// </summary>
    int QueueDepth { get; }

    /// <summary>
    /// Total number of successful background refreshes since startup.
    /// </summary>
    long SuccessCount { get; }

    /// <summary>
    /// Total number of failed background refreshes since startup.
    /// </summary>
    long FailureCount { get; }

    /// <summary>
    /// Total number of background refreshes that completed but skipped write-back
    /// because the key was invalidated while the refresh was in-flight.
    /// </summary>
    long SkippedCount { get; }

    /// <summary>
    /// Attempts to enqueue a background refresh for the given cache key.
    /// Returns false if a refresh for this key is already pending (deduplication)
    /// or if the key is still within a temporary retry backoff after a recent failure.
    /// </summary>
    /// <param name="key">Cache key being refreshed</param>
    /// <param name="refreshCallback">Async callback that performs the actual refresh</param>
    /// <returns>True if the refresh was enqueued, false if deduplicated</returns>
    bool TryEnqueueRefresh(string key, Func<CancellationToken, Task> refreshCallback);

    /// <summary>
    /// Marks a cache key as explicitly invalidated while a refresh may be pending.
    /// If a pending refresh for this key subsequently fails, stale-value restoration is skipped.
    /// </summary>
    /// <param name="key">Cache key that was invalidated</param>
    void NotifyInvalidation(string key);

    /// <summary>
    /// Returns true if the given key was explicitly invalidated while a refresh was pending.
    /// </summary>
    /// <param name="key">Cache key to check</param>
    /// <returns>True if the key was invalidated after a refresh was enqueued</returns>
    bool WasInvalidated(string key);

    /// <summary>
    /// Atomically claims write-back rights for a completed refresh.
    /// Returns true only if the key has not been invalidated since the refresh was enqueued.
    /// The caller must call this immediately before the write-back to close the race window
    /// between the invalidation check and the cache write.
    /// </summary>
    /// <param name="key">Cache key to claim</param>
    /// <returns>True if the claim succeeded (safe to write), false if invalidated</returns>
    bool TryClaimWriteBack(string key);
}
