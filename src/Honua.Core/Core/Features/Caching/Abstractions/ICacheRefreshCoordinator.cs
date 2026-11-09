// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Caching.Abstractions;

public interface ICacheRefreshCoordinator
{
    int QueueDepth { get; }

    long SuccessCount { get; }

    long FailureCount { get; }

    // A completed refresh skips write-back when the key was invalidated while the refresh was in flight.
    long SkippedCount { get; }

    // Enqueue returns false when a refresh for the key is already pending or the key is in retry backoff.
    bool TryEnqueueRefresh(string key, Func<CancellationToken, Task> refreshCallback);

    void NotifyInvalidation(string key);

    bool WasInvalidated(string key);

    // Claim write-back immediately before the cache write; it succeeds only if the key was not invalidated after enqueue.
    bool TryClaimWriteBack(string key);

    ValueTask<bool> WasInvalidatedAsync(string key, CancellationToken cancellationToken = default)
        => new(WasInvalidated(key));

    // A distributed claim must not block a thread; the default implementation calls the synchronous method.
    ValueTask<bool> TryClaimWriteBackAsync(string key, CancellationToken cancellationToken = default)
        => new(TryClaimWriteBack(key));
}
