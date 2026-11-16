// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Coordination;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.SharedCaching;

// Whether an edit made now would reach the CDN as a tag purge. Purge-backed lifetimes are only given while it would:
// otherwise an edit could stay hidden behind a day-long CDN copy. Every doubt answers no, and the answer is cheap
// because requests read it: the shared signals (budget counter, any task's recent purge failure) are read in the
// background at most once a minute, and an answer older than a few reads counts as no.
internal sealed partial class CdnPurgeReliability(
    ICdnInvalidator invalidator,
    ILogger<CdnPurgeReliability> logger,
    CdnPurgeCoordination? coordination = null)
{
    internal const string FailureLeaseKey = CdnInvalidationService.LeaseKeyPrefix + "failed";

    internal static readonly TimeSpan SharedReadInterval = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan SharedReadTimeout = TimeSpan.FromSeconds(10);

    // After a failed purge, on any task, long lifetimes stop for at least this long; responses already sent keep theirs.
    // The task whose purges fail holds off until one is delivered, and keeps the shared lease held meanwhile.
    internal static readonly TimeSpan FailureHoldOff = TimeSpan.FromMinutes(15);

    // The probe holds the failure lease this long at most; a failure recorded meanwhile tries again after it.
    internal static readonly TimeSpan ProbeHold = TimeSpan.FromSeconds(1);

    private const int StaleAfterReads = 3;

    private readonly TimeProvider _clock = coordination?.TimeProvider ?? TimeProvider.System;
    private readonly IOptionsMonitor<SharedCacheOptions>? _options = coordination?.Options;
    private readonly ICdnPurgeBudget? _budget = coordination?.Budget;
    private readonly ILeaseStore? _leases = coordination?.LeaseStore;
    private readonly Lock _gate = new();
    private readonly string _failureOwner = "failure:" + Guid.NewGuid().ToString("N");
    private DateTimeOffset _sharedReadAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastFailure = DateTimeOffset.MinValue;
    private bool _sharedSignalsAllow;
    private bool _failing;
    private Task? _renewal;
    private long _version;
    private Task? _sharedRead;

    internal bool PurgesReachCdn()
    {
        if (invalidator is NoCdnInvalidator || _options?.CurrentValue is not { CacheTagsEnabled: true } options)
        {
            return false;
        }

        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (FailingLocked(now))
            {
                return false;
            }

            var age = now - _sharedReadAt;
            if (age >= SharedReadInterval && _sharedRead is null)
            {
                _sharedRead = ReadSharedInBackgroundAsync(options.PurgeBudget);
            }

            return _sharedSignalsAllow && age < SharedReadInterval * StaleAfterReads;
        }
    }

    // A failed or still-busy purge: this task stops at once, and the other tasks at their next read.
    internal void RecordFailedPurge()
    {
        // Only a deployment that gives purge-backed lifetimes needs the other tasks to know.
        var share = _options?.CurrentValue is { CacheTagsEnabled: true, PurgeBacked.Configured: true };
        lock (_gate)
        {
            _lastFailure = _clock.GetUtcNow();
            _failing = true;
            if (share)
            {
                _renewal ??= HoldFailureLeaseWhileFailingAsync();
            }
        }
    }

    internal void RecordDeliveredPurge()
    {
        lock (_gate)
        {
            _failing = false;
        }
    }

    // The budget was found spent by a purge on this task; no need to wait for the next read.
    internal void RecordBudgetSpent()
    {
        lock (_gate)
        {
            _sharedSignalsAllow = false;
            _version++;
        }
    }

    // A read that started before RecordBudgetSpent does not undo it.
    internal async Task ReadSharedSignalsAsync(CdnPurgeBudgetOptions budget, CancellationToken cancellationToken)
    {
        long version;
        lock (_gate)
        {
            version = _version;
        }

        // Real time, not the injected clock: a hung read must end even where the clock is driven by hand.
        using var timeout = new CancellationTokenSource(SharedReadTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var allow = await HasBudgetHeadroomAsync(budget, linked.Token).ConfigureAwait(false)
                    && !await AnyTaskFailedRecentlyAsync(linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_version == version)
            {
                _sharedSignalsAllow = allow;
            }

            _sharedReadAt = _clock.GetUtcNow();
        }
    }

    private async Task ReadSharedInBackgroundAsync(CdnPurgeBudgetOptions budget)
    {
        await Task.Yield();
        try
        {
            await ReadSharedSignalsAsync(budget, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _sharedRead = null;
            }
        }
    }

    private async Task<bool> HasBudgetHeadroomAsync(CdnPurgeBudgetOptions budget, CancellationToken cancellationToken)
    {
        // Without a counter or with the cap off, no edit purge is ever refused for budget.
        if (_budget is null || budget.MonthlyEditSubmissions <= 0)
        {
            return true;
        }

        try
        {
            var month = await _budget.ReadCurrentMonthAsync(cancellationToken).ConfigureAwait(false);
            if (month is { Decision: CdnPurgeBudgetDecision.Unavailable })
            {
                return false;
            }

            var submitted = month?.SubmittedCount ?? 0;
            var hasHeadroom = submitted < budget.MonthlyEditSubmissions - SharedCachePurgeBacked.BudgetReserve(budget.MonthlyEditSubmissions);
            if (!hasHeadroom)
            {
                Log.PurgeBackedLifetimesPaused(logger, submitted, budget.MonthlyEditSubmissions);
            }

            return hasHeadroom;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.SharedSignalsUnreadable(logger, exception);
            return false;
        }
    }

    // The failure lease is held for the hold-off by whichever task failed. Taking it briefly shows nobody holds it.
    private async Task<bool> AnyTaskFailedRecentlyAsync(CancellationToken cancellationToken)
    {
        if (_leases is null)
        {
            return false;
        }

        try
        {
            var probe = new LeaseClaim(FailureLeaseKey, "probe:" + Guid.NewGuid().ToString("N"));
            if (!await _leases.TryAcquireAsync(probe, ProbeHold, cancellationToken).ConfigureAwait(false))
            {
                return true;
            }

            await _leases.ReleaseAsync(probe, cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.SharedSignalsUnreadable(logger, exception);
            return true;
        }
    }

    private bool FailingLocked(DateTimeOffset now) => _failing || now - _lastFailure < FailureHoldOff;

    // Keeps the shared failure lease held from the first failure until a purge is delivered, renewing it every read
    // interval whatever the request traffic, so the other tasks hold off for exactly as long as this one does.
    private async Task HoldFailureLeaseWhileFailingAsync()
    {
        await Task.Yield();
        try
        {
            while (true)
            {
                await HoldFailureLeaseAsync().ConfigureAwait(false);
                await Task.Delay(SharedReadInterval, _clock).ConfigureAwait(false);
                lock (_gate)
                {
                    if (!_failing)
                    {
                        _renewal = null;
                        return;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.SharedSignalsUnreadable(logger, exception);
            lock (_gate)
            {
                _renewal = null;
            }
        }
    }

    // Takes the failure lease, or extends it when this task already holds it. Refused while another task's failure
    // holds it (already shared) or while a probe does, in which case it tries once more after the probe.
    private async Task HoldFailureLeaseAsync()
    {
        if (_leases is null)
        {
            return;
        }

        var failure = new LeaseClaim(FailureLeaseKey, _failureOwner);
        try
        {
            if (await TakeOrRenewAsync(failure).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(ProbeHold * 2, _clock).ConfigureAwait(false);
            if (!await TakeOrRenewAsync(failure).ConfigureAwait(false))
            {
                Log.FailureLeaseHeldElsewhere(logger);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Log.SharedSignalsUnreadable(logger, exception);
        }
    }

    private async Task<bool> TakeOrRenewAsync(LeaseClaim failure)
        => await _leases!.TryAcquireAsync(failure, FailureHoldOff).ConfigureAwait(false)
           || await _leases.RenewAsync(failure, FailureHoldOff).ConfigureAwait(false);

    private static partial class Log
    {
        [LoggerMessage(
            EventId = 10316,
            Level = LogLevel.Information,
            Message = "Purge-backed CDN lifetimes are paused: the monthly edit-purge budget is nearly spent. submitted={Submitted} limit={Limit}")]
        public static partial void PurgeBackedLifetimesPaused(ILogger logger, int submitted, int limit);

        [LoggerMessage(
            EventId = 10317,
            Level = LogLevel.Warning,
            Message = "The CDN purge budget or the shared purge-failure lease could not be read; responses get the ordinary CDN lifetimes until they can.")]
        public static partial void SharedSignalsUnreadable(ILogger logger, Exception exception);

        [LoggerMessage(
            EventId = 10322,
            Level = LogLevel.Debug,
            Message = "The shared CDN purge-failure lease is held by another task or a probe; that task's hold-off applies.")]
        public static partial void FailureLeaseHeldElsewhere(ILogger logger);
    }
}
