// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Coordination;

// One owner's hold on one lease. A store failure counts as a lost lease: the holder cannot prove
// it still owns the lease, so work guarded by LeaseLostToken stops and the next attempt retries.
public sealed class LeaseHolder(ILeaseStore leases, string leaseKey, TimeSpan leaseDuration) : IDisposable
{
    private readonly LeaseClaim _claim = new(leaseKey, Guid.NewGuid().ToString("N"));
    private readonly object _stateLock = new();
    private CancellationTokenSource _leaseLost = new();
    private volatile bool _hasLease;

    public bool HasLease => _hasLease;

    public CancellationToken LeaseLostToken
    {
        get
        {
            lock (_stateLock)
            {
                return _leaseLost.Token;
            }
        }
    }

    public async Task<bool> TryAcquireOrExtendAsync(CancellationToken cancellationToken = default)
    {
        bool held;
        try
        {
            held = (HasLease && await leases.RenewAsync(_claim, leaseDuration, cancellationToken).ConfigureAwait(false))
                || await leases.TryAcquireAsync(_claim, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException and not OperationCanceledException)
        {
            held = false;
        }

        if (held)
        {
            MarkAcquired();
        }
        else
        {
            MarkLost();
        }

        return held;
    }

    public async Task MaintainAsync(TimeSpan renewalInterval, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(renewalInterval, TimeSpan.Zero);

        try
        {
            while (HasLease)
            {
                await Task.Delay(renewalInterval, cancellationToken).ConfigureAwait(false);
                if (!await TryAcquireOrExtendAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped maintaining; it releases the lease or lets it expire.
        }
    }

    public async Task ReleaseAsync()
    {
        try
        {
            if (HasLease)
            {
                await leases.ReleaseAsync(_claim).ConfigureAwait(false);
            }
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException)
        {
            // The lease expires on its own.
        }
        finally
        {
            MarkLost();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _leaseLost.Dispose();
        }
    }

    private void MarkAcquired()
    {
        lock (_stateLock)
        {
            if (_leaseLost.IsCancellationRequested)
            {
                _leaseLost.Dispose();
                _leaseLost = new CancellationTokenSource();
            }

            _hasLease = true;
        }
    }

    private void MarkLost()
    {
        CancellationTokenSource? toCancel = null;
        lock (_stateLock)
        {
            _hasLease = false;
            if (!_leaseLost.IsCancellationRequested)
            {
                toCancel = _leaseLost;
            }
        }

        toCancel?.Cancel();
    }
}
