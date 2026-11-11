// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;

namespace Honua.Core.Features.Infrastructure.Coordination;

// Leadership is a lease: at most one instance holds it at any store time, and a leader that stops
// renewing is replaced within the lease duration. Leadership alone does not fence a write; work a
// leader does must be idempotent or guarded by its own record.
public sealed class LeaseLeaderElection(ILeaseStore leases, string leadershipKey, TimeSpan leaseDuration)
    : IDistributedLeaderElection
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    private volatile bool _isLeader;

    public bool IsLeader => _isLeader;

    public string InstanceId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";

    private LeaseClaim Claim => new(leadershipKey, InstanceId);

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        _isLeader = (_isLeader && await leases.RenewAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false))
            || await leases.TryAcquireAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false);
        return _isLeader;
    }

    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        _isLeader = _isLeader && await leases.RenewAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false);
        return _isLeader;
    }

    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        _isLeader = false;
        await leases.ReleaseAsync(Claim, cancellationToken).ConfigureAwait(false);
    }
}
