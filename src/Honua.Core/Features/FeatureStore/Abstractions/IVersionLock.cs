// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Durable mutual-exclusion lock keyed by (service, version) that serializes branch-version
/// reconcile/post and conflicting conflict-resolution work (#1553). Only one reconcile, post, or
/// conflicting resolve may hold the lock for a given version at a time; a competing acquisition fails
/// fast so the caller can return a clear in-progress (409) response or queue an async job.
/// </summary>
/// <remarks>
/// The production implementation is Redis-backed (reusing the existing StackExchange.Redis lock
/// primitive) so the lock is honored across every server replica. When Redis is not configured the
/// in-process implementation degrades to a single-node mutex, which is correct for single-node and
/// development/test deployments. The lock is advisory over the version-maintenance workflow only; it is
/// never taken on the DEFAULT read/edit path, so DEFAULT behavior stays byte-identical.
/// </remarks>
public interface IVersionLock
{
    /// <summary>
    /// Attempts to acquire the version lock without blocking. Returns a disposable handle that releases
    /// the lock when disposed, or null when the lock is already held for the (service, version) pair.
    /// </summary>
    /// <param name="service">Owning service identity (lock scope).</param>
    /// <param name="versionId">Branch version to lock.</param>
    /// <param name="leaseDuration">
    /// How long the lock is held before it auto-expires if not released (crash safety). The handle
    /// renews the lease while it is alive.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A lock handle on success, or null when the lock is contended.</returns>
    Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A held version lock (#1553). Disposing the handle releases the lock; the lock also auto-expires after
/// its lease if the holder crashes before releasing.
/// </summary>
public interface IVersionLockHandle : IAsyncDisposable
{
}
