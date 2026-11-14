// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Core.Features.Realtime;

namespace Honua.Server.Features.Realtime;

// PostgreSQL decides ownership. The local deadline is conservative: it starts before each store
// call, so a delayed reply never extends delivery beyond the store's expiry. Loss is terminal;
// a stream must reconnect, rather than silently acquire a new ownership epoch.
internal sealed class RealtimeStreamLease : IRealtimeStreamLease
{
    private readonly TimeSpan _duration;
    private readonly TimeSpan _renewalInterval;
    private readonly ILeaseStore _leases;
    private readonly LeaseClaim _claim;
    private readonly CancellationTokenSource _lost = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _renewal;
    private long _confirmedAt;
    private int _disposed;

    private RealtimeStreamLease(ILeaseStore leases, LeaseClaim claim, long confirmedAt, TimeSpan duration, TimeSpan renewalInterval)
    {
        _duration = duration;
        _renewalInterval = renewalInterval;
        _leases = leases;
        _claim = claim;
        _confirmedAt = confirmedAt;
        ScheduleDeadline(confirmedAt);
        _renewal = RenewAsync();
    }

    public bool IsHeld => !_lost.IsCancellationRequested && Stopwatch.GetElapsedTime(Interlocked.Read(ref _confirmedAt)) < _duration;

    public CancellationToken LostToken => _lost.Token;

    internal static async ValueTask<IRealtimeStreamLease?> TryAcquireAsync(
        ILeaseStore leases, string sessionId, CancellationToken cancellationToken,
        TimeSpan? duration = null, TimeSpan? renewalInterval = null)
    {
        var lifetime = duration ?? TimeSpan.FromSeconds(30);
        var interval = renewalInterval ?? TimeSpan.FromSeconds(10);
        if (lifetime <= TimeSpan.Zero || interval <= TimeSpan.Zero || interval >= lifetime)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }
        // Hashing keeps arbitrary session IDs inside the shared store's bounded key contract.
        var key = "realtime:mcp-stream:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)));
        var claim = new LeaseClaim(key, Guid.NewGuid().ToString("N"));
        var before = Stopwatch.GetTimestamp();
        if (!await leases.TryAcquireAsync(claim, lifetime, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (Stopwatch.GetElapsedTime(before) >= lifetime)
        {
            // The acquisition may already have expired; it is never safe to deliver on it.
            await leases.ReleaseAsync(claim, cancellationToken).ConfigureAwait(false);
            return null;
        }

        return new RealtimeStreamLease(leases, claim, before, lifetime, interval);
    }

    private void ScheduleDeadline(long confirmedAt)
    {
        var remaining = _duration - Stopwatch.GetElapsedTime(confirmedAt);
        _lost.CancelAfter(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
    }

    private async Task RenewAsync()
    {
        using var renewal = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _lost.Token);
        try
        {
            while (IsHeld)
            {
                await Task.Delay(_renewalInterval, renewal.Token).ConfigureAwait(false);
                var before = Stopwatch.GetTimestamp();
                if (!IsHeld || !await _leases.RenewAsync(_claim, _duration, renewal.Token).ConfigureAwait(false)
                    || !IsHeld || Stopwatch.GetElapsedTime(before) >= _duration)
                {
                    break;
                }

                Interlocked.Exchange(ref _confirmedAt, before);
                ScheduleDeadline(before);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Cancellation or a store failure both mean the stream must stop delivering.
        }
        finally
        {
            await _lost.CancelAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lost.CancelAsync().ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        await _renewal.ConfigureAwait(false);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _leases.ReleaseAsync(_claim, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An unreachable database expires this claim without releasing a successor's claim.
        }

        _stop.Dispose();
        _lost.Dispose();
    }
}
