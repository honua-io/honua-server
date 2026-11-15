// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using StackExchange.Redis;

namespace Honua.Infrastructure.Events;

internal sealed class RedisLeaseCoordinator(
    IConnectionMultiplexer? redis,
    string leaseKey,
    TimeSpan leaseDuration) : IDisposable
{
    private readonly IDatabase? _database = redis?.GetDatabase();
    private readonly string _leaseKey = leaseKey;
    private readonly TimeSpan _leaseDuration = leaseDuration;
    private readonly string _ownerId = Guid.NewGuid().ToString("N");
    private readonly object _leaseStateLock = new();
    private CancellationTokenSource _leaseLostCts = new();
    private volatile bool _hasLease;

    public bool IsConfigured => _database != null;
    public bool HasLease => _hasLease;
    internal CancellationToken LeaseLostToken
    {
        get
        {
            lock (_leaseStateLock)
            {
                return _leaseLostCts.Token;
            }
        }
    }

    public async Task<bool> TryAcquireOrExtendAsync()
    {
        if (_database == null)
        {
            return false;
        }

        try
        {
            var hadLease = HasLease;
            var acquired = hadLease
                ? await _database.LockExtendAsync(_leaseKey, _ownerId, _leaseDuration).ConfigureAwait(false)
                : await _database.LockTakeAsync(_leaseKey, _ownerId, _leaseDuration).ConfigureAwait(false);

            if (!acquired)
            {
                acquired = await _database.LockTakeAsync(_leaseKey, _ownerId, _leaseDuration).ConfigureAwait(false);
            }

            if (acquired)
            {
                MarkLeaseAcquired();
            }
            else
            {
                MarkLeaseLost();
            }

            return acquired;
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Intentional: any Redis failure here (timeout, connection drop) is treated as a
            // lost lease rather than a fatal error — the caller (dispatcher loop) retries the
            // acquire/extend on its next iteration. No ILogger is wired into this coordinator;
            // MarkLeaseLost() already flips HasLease/LeaseLostToken so callers observe the state.
            MarkLeaseLost();
            return false;
        }
    }

    public async Task ReleaseAsync()
    {
        if (_database == null || !HasLease)
        {
            MarkLeaseLost();
            return;
        }

        try
        {
            await _database.LockReleaseAsync(_leaseKey, _ownerId).ConfigureAwait(false);
        }
        catch (Exception caughtException) when (caughtException is not OutOfMemoryException)
        {
            // Ignore release failures; the lease will expire.
        }
        finally
        {
            MarkLeaseLost();
        }
    }

    private void MarkLeaseAcquired()
    {
        lock (_leaseStateLock)
        {
            if (_leaseLostCts.IsCancellationRequested)
            {
                _leaseLostCts.Dispose();
                _leaseLostCts = new CancellationTokenSource();
            }

            _hasLease = true;
        }
    }

    private void MarkLeaseLost()
    {
        CancellationTokenSource? leaseLostCts = null;
        lock (_leaseStateLock)
        {
            _hasLease = false;
            if (!_leaseLostCts.IsCancellationRequested)
            {
                leaseLostCts = _leaseLostCts;
            }
        }

        leaseLostCts?.Cancel();
    }

    public void Dispose()
    {
        lock (_leaseStateLock)
        {
            _leaseLostCts.Dispose();
        }
    }
}
