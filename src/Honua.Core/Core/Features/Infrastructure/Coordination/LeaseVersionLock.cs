// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Abstractions;

namespace Honua.Core.Features.Infrastructure.Coordination;

public sealed class LeaseVersionLock(ILeaseStore leases) : IVersionLock
{
    private const int RenewalsPerLease = 3;

    public async Task<IVersionLockHandle?> TryAcquireAsync(
        string service,
        Guid versionId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var holder = new LeaseHolder(leases, $"version-lock:{service}:{versionId:N}", leaseDuration);
        if (!await holder.TryAcquireOrExtendAsync(cancellationToken).ConfigureAwait(false))
        {
            holder.Dispose();
            return null;
        }

        return new Handle(holder, leaseDuration / RenewalsPerLease);
    }

    private sealed class Handle : IVersionLockHandle
    {
        private readonly LeaseHolder _holder;
        private readonly CancellationTokenSource _released = new();
        private readonly Task _renewal;

        public Handle(LeaseHolder holder, TimeSpan renewalInterval)
        {
            _holder = holder;
            _renewal = holder.MaintainAsync(renewalInterval, _released.Token);
        }

        public async ValueTask DisposeAsync()
        {
            await _released.CancelAsync().ConfigureAwait(false);
            await _renewal.ConfigureAwait(false);
            await _holder.ReleaseAsync().ConfigureAwait(false);
            _holder.Dispose();
            _released.Dispose();
        }
    }
}
