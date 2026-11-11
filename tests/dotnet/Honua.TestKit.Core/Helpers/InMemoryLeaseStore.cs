// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Coordination;

namespace Honua.TestKit.Helpers;

// Lease rows for one process: an owner holds a key until it releases it or the lease expires.
public sealed class InMemoryLeaseStore(TimeProvider? timeProvider = null) : ILeaseStore
{
    private readonly Dictionary<string, (string OwnerId, DateTimeOffset ExpiresAt)> _leases = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<bool> TryAcquireAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(claim.LeaseKey, out var held) && held.ExpiresAt > _timeProvider.GetUtcNow() && held.OwnerId != claim.OwnerId)
            {
                return Task.FromResult(false);
            }

            _leases[claim.LeaseKey] = (claim.OwnerId, _timeProvider.GetUtcNow().Add(duration));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RenewAsync(LeaseClaim claim, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_leases.TryGetValue(claim.LeaseKey, out var held) || held.OwnerId != claim.OwnerId || held.ExpiresAt <= _timeProvider.GetUtcNow())
            {
                return Task.FromResult(false);
            }

            _leases[claim.LeaseKey] = (claim.OwnerId, _timeProvider.GetUtcNow().Add(duration));
            return Task.FromResult(true);
        }
    }

    public Task ReleaseAsync(LeaseClaim claim, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(claim.LeaseKey, out var held) && held.OwnerId == claim.OwnerId)
            {
                _leases.Remove(claim.LeaseKey);
            }

            return Task.CompletedTask;
        }
    }
}
