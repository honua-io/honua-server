// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;

namespace Honua.Core.Features.Infrastructure.Coordination;

// Leadership is a lease: at most one instance holds it at any store time, and a leader that stops
// renewing is replaced within the lease duration. Leadership alone does not fence a write; work a
// leader does must be idempotent or guarded by its own record.
// A store failure ends leadership, because the instance can no longer prove it holds the lease. The
// lease may still be its own, so the next acquire renews it before competing for it.
public sealed class LeaseLeaderElection(ILeaseStore leases, string leadershipKey, TimeSpan leaseDuration)
    : IDistributedLeaderElection
{
    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(30);

    private volatile bool _isLeader;
    private volatile bool _mayHoldLease;

    public bool IsLeader => _isLeader;

    public static string NodeId { get; } = $"{Environment.MachineName}-{Environment.ProcessId}";

    public string InstanceId { get; } = $"{NodeId}-{Guid.NewGuid():N}";

    private LeaseClaim Claim => new(leadershipKey, InstanceId);

    public async Task<bool> TryAcquireLeadershipAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _isLeader = ((_isLeader || _mayHoldLease) && await leases.RenewAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false))
                || await leases.TryAcquireAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false);
            _mayHoldLease = false;
            return _isLeader;
        }
        catch
        {
            StepDown();
            throw;
        }
    }

    public async Task<bool> HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (!_isLeader)
        {
            return false;
        }

        try
        {
            _isLeader = await leases.RenewAsync(Claim, leaseDuration, cancellationToken).ConfigureAwait(false);
            return _isLeader;
        }
        catch
        {
            StepDown();
            throw;
        }
    }

    public async Task ReleaseLeadershipAsync(CancellationToken cancellationToken = default)
    {
        _isLeader = false;
        _mayHoldLease = false;
        try
        {
            await leases.ReleaseAsync(Claim, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException)
        {
            // The lease expires on its own.
        }
    }

    private void StepDown()
    {
        _isLeader = false;
        _mayHoldLease = true;
    }
}
