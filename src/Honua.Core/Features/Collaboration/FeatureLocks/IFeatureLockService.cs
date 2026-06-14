// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

/// <summary>
/// Coordinates collaborative editing locks on individual features.
/// </summary>
public interface IFeatureLockService
{
    /// <summary>
    /// Claims a lock on a feature, or renews it when already held by the same holder.
    /// </summary>
    /// <param name="feature">The feature to lock.</param>
    /// <param name="holder">The principal requesting the lock.</param>
    /// <param name="leaseDuration">The requested lease duration.</param>
    /// <param name="access">The caller's authorization context.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The outcome of the claim attempt.</returns>
    ValueTask<FeatureLockClaimResponse> ClaimAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews an existing lock held by the supplied holder.
    /// </summary>
    /// <param name="feature">The locked feature.</param>
    /// <param name="holder">The principal that holds the lock.</param>
    /// <param name="leaseDuration">The new lease duration measured from now.</param>
    /// <param name="access">The caller's authorization context.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The outcome of the renewal attempt.</returns>
    ValueTask<FeatureLockRenewResponse> RenewAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases a lock held by the supplied holder.
    /// </summary>
    /// <param name="feature">The locked feature.</param>
    /// <param name="holder">The principal that holds the lock.</param>
    /// <param name="access">The caller's authorization context.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The outcome of the release attempt.</returns>
    ValueTask<FeatureLockReleaseResponse> ReleaseAsync(
        FeatureRef feature,
        LockHolder holder,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active lease for a feature, if one exists.
    /// </summary>
    /// <param name="feature">The feature to inspect.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The active lease, or <see langword="null"/> when none is held.</returns>
    ValueTask<FeatureLockLease?> GetActiveLeaseAsync(
        FeatureRef feature,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes all expired leases.
    /// </summary>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The number of leases that were pruned.</returns>
    ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}
