// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using StackExchange.Redis;

namespace Honua.Server.Features.Infrastructure.Events;

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
    private bool _hasLease;

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
            var acquired = _hasLease
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
            MarkLeaseLost();
            return false;
        }
    }

    internal async Task MaintainLeaseAsync(TimeSpan renewalInterval, CancellationToken cancellationToken)
    {
        if (_database == null || !_hasLease)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(renewalInterval, TimeSpan.Zero);

        try
        {
            while (!cancellationToken.IsCancellationRequested && _hasLease)
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
        }
    }

    public async Task ReleaseAsync()
    {
        if (_database == null || !_hasLease)
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
