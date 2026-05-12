// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Collaboration.FeatureLocks;

public interface IFeatureLockService
{
    ValueTask<FeatureLockClaimResponse> ClaimAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    ValueTask<FeatureLockRenewResponse> RenewAsync(
        FeatureRef feature,
        LockHolder holder,
        TimeSpan leaseDuration,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    ValueTask<FeatureLockReleaseResponse> ReleaseAsync(
        FeatureRef feature,
        LockHolder holder,
        FeatureLockAccessContext access,
        CancellationToken cancellationToken = default);

    ValueTask<FeatureLockLease?> GetActiveLeaseAsync(
        FeatureRef feature,
        CancellationToken cancellationToken = default);

    ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}
