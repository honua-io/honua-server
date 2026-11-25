// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Coordination;

// One owner's hold on one lease. A store failure counts as a lost lease: the holder cannot prove
// it still owns the lease, so work guarded by LeaseLostToken stops and the next attempt retries.
public sealed class LeaseHolder(ILeaseStore leases, string leaseKey, TimeSpan leaseDuration) : IDisposable
{
    private readonly LeaseClaim _claim = new(leaseKey, Guid.NewGuid().ToString("N"));
    private readonly object _stateLock = new();
    private LossSignal _signal = new();
    private volatile bool _hasLease;
    private bool _disposed;

    public bool HasLease => _hasLease;

    public CancellationToken LeaseLostToken
    {
        get
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _signal.Source.Token;
            }
        }
    }

    public Task<bool> TryAcquireOrExtendAsync(CancellationToken cancellationToken = default)
        => HasLease
            ? RenewOnlyAsync(cancellationToken)
            : TryAcquireAsync(cancellationToken);

    public async Task<bool> TryAcquireAsync(CancellationToken cancellationToken = default)
    {
        bool held;
        try
        {
            held = await leases.TryAcquireAsync(_claim, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException
            && (storeFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
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

    public async Task<bool> RenewOnlyAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLease)
        {
            MarkLost();
            return false;
        }

        bool held;
        try
        {
            held = await leases.RenewAsync(_claim, leaseDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException
            && (storeFailure is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
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
                if (!await RenewOnlyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller stopped maintaining; it releases the lease or lets it expire.
        }
        catch (Exception storeFailure) when (storeFailure is not OutOfMemoryException)
        {
            MarkLost();
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
        LossSignal? toDispose = null;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _hasLease = false;
            _signal.Retired = true;
            if (!_signal.Cancelling)
            {
                toDispose = _signal;
            }
        }

        toDispose?.Source.Dispose();
    }

    private void MarkAcquired()
    {
        LossSignal? toDispose = null;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            if (_signal.Cancelled || _signal.Source.IsCancellationRequested)
            {
                var previous = _signal;
                previous.Retired = true;
                if (!previous.Cancelling)
                {
                    toDispose = previous;
                }

                _signal = new LossSignal();
            }

            _hasLease = true;
        }

        toDispose?.Source.Dispose();
    }

    private void MarkLost()
    {
        LossSignal? toCancel = null;
        lock (_stateLock)
        {
            _hasLease = false;
            if (_disposed || _signal.Cancelled)
            {
                return;
            }

            _signal.Cancelled = true;
            _signal.Cancelling = true;
            toCancel = _signal;
        }

        try
        {
            toCancel.Source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The signal was disposed concurrently while firing lease loss.
        }
        finally
        {
            bool disposeNow;
            lock (_stateLock)
            {
                toCancel.Cancelling = false;
                disposeNow = toCancel.Retired;
            }

            if (disposeNow)
            {
                toCancel.Source.Dispose();
            }
        }
    }

    private sealed class LossSignal
    {
        public CancellationTokenSource Source { get; } = new();
        public bool Cancelled;
        public bool Cancelling;
        public bool Retired;
    }
}
