// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

    // An implementation that cannot answer cheaply reports that a lease may exist, so every feature is still checked.
    ValueTask<bool> HasAnyActiveLeasesAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(true);

    ValueTask<int> PruneExpiredAsync(CancellationToken cancellationToken = default);
}
