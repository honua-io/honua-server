// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using StackExchange.Redis;

namespace Honua.Infrastructure.Events;

/// <summary>
/// Best-effort Redis-backed lease coordinator for single-writer background dispatchers.
/// </summary>
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
        catch
        {
            // Intentional: any Redis failure here (timeout, connection drop) is treated as a
            // lost lease rather than a fatal error — the caller (dispatcher loop) retries the
            // acquire/extend on its next iteration. No ILogger is wired into this coordinator;
            // MarkLeaseLost() already flips HasLease/LeaseLostToken so callers observe the state.
            MarkLeaseLost();
            return false;
        }
    }

    internal async Task MaintainLeaseAsync(TimeSpan renewalInterval, CancellationToken cancellationToken)
    {
        if (_database == null || !HasLease)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(renewalInterval, TimeSpan.Zero);

        try
        {
            while (!cancellationToken.IsCancellationRequested && HasLease)
            {
                await Task.Delay(renewalInterval, cancellationToken).ConfigureAwait(false);
                if (!await TryAcquireOrExtendAsync().ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Intentional no-op: this fires on ordinary host shutdown (the Task.Delay above
            // observes cancellationToken). There is nothing to clean up here — the lease
            // either expires naturally in Redis or is released explicitly via ReleaseAsync()
            // by the caller's finally block — so swallowing and letting the loop exit is safe.
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
        catch
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
