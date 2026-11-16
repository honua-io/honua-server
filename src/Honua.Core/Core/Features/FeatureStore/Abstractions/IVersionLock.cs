// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

public interface IVersionLock
{
    Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

public interface IVersionLockHandle : IAsyncDisposable
{
    // Cancelled once the holder cannot prove it owns the lock, after which another holder
    // may acquire it. Work protected by the lock observes this token.
    CancellationToken LockLostToken { get; }
}
