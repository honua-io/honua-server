// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.SharedCaching;

// What purge-backed lifetimes add to purges, where a lost purge would leave a layer stale for the long lifetime rather
// than minutes: a failed edit purge is retried with backoff; a sent purge is repeated once after a settle delay, for a
// response rendered from pre-change state that reached the CDN after the first purge; and purges still waiting when the
// task stops are sent then. The retries and settle repeats run only while purge-backed lifetimes can be given; the
// shutdown flush runs for every deployment with a CDN, where it also saves the ordinary edit purges a scale-in loses.
internal sealed partial class CdnInvalidationService
{
    // A stopping task has little time left, and the host's own deadline may already be close.
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(10);

    private readonly PurgeBackedDelivery _delivery = new(coordination?.TimeProvider ?? TimeProvider.System);

    // Set by the shutdown flush. A send still in flight then has no timer left to retry or settle on, so whatever it
    // would schedule is logged as not sent instead.
    private volatile bool _stopping;

    private SharedCachePurgeBacked? ActivePurgeBacked
        => _options?.CurrentValue is { CacheTagsEnabled: true, PurgeBacked: { Configured: true } purgeBacked } ? purgeBacked : null;

    // Sends every purge this task still holds: pending in a window (retries included) or waiting to settle. Leases are
    // not claimed and a busy CDN is not waited for: a purge sent twice costs less than one lost.
    internal async Task FlushPendingAsync()
    {
        PurgeBatch[] pending;
        string[] paths;
        lock (_gate)
        {
            _stopping = true;
            foreach (var batch in _delivery.TakeAll())
            {
                AddPendingPurges(batch.Paths, batch.Cause);
            }

            pending = PurgeBatch.Group(_pendingEditPaths);
            paths = [.. _pendingEditPaths.Keys];
            _pendingEditPaths.Clear();
            _windowClosesAt = null;
        }

        CancelBackgroundTimers();

        if (paths.Length == 0 || !CdnConfigured)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(ShutdownFlushTimeout);
        try
        {
            foreach (var batch in pending)
            {
                try
                {
                    var batches = await SendWithinLimitsAsync(batch.Paths, batch.Cause.AsShutdown(), deadline.Token)
                        .ConfigureAwait(false);
                    if (batches.Result.Failed)
                    {
                        Log.ShutdownPurgesNotSent(logger, batch.Paths.Length, string.Join(' ', batch.Paths));
                    }
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Log.BackgroundPurgeFailed(logger, ex);
                    _reliability?.RecordFailedPurge();
                    Log.ShutdownPurgesNotSent(logger, batch.Paths.Length, string.Join(' ', batch.Paths));
                }
            }
        }
        catch (OperationCanceledException)
        {
            Log.ShutdownPurgesNotSent(logger, paths.Length, string.Join(' ', paths));
        }
    }

    private void CancelBackgroundTimers()
    {
        _shutdownSignal.TrySetResult();
    }

    private async Task<bool> WaitUntilDueOrShutdownAsync(TimeSpan delay)
    {
        if (_stopping || _applicationStopping.IsCancellationRequested)
        {
            return false;
        }

        if (delay <= TimeSpan.Zero)
        {
            return true;
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_applicationStopping);
        var delayTask = Task.Delay(delay, _timeProvider, delayCts.Token);
        var winner = await Task.WhenAny(delayTask, _shutdownSignal.Task).ConfigureAwait(false);
        if (winner == _shutdownSignal.Task || _stopping || _applicationStopping.IsCancellationRequested)
        {
            await delayCts.CancelAsync().ConfigureAwait(false);
            return false;
        }

        try
        {
            await delayTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping || _applicationStopping.IsCancellationRequested)
        {
            return false;
        }

        return !_stopping && !_applicationStopping.IsCancellationRequested;
    }

    // Edits made while purge-backed lifetimes are on restart the give-up clock of a path that is failing.
    private void NoteEditQueued(IEnumerable<string> paths)
    {
        if (ActivePurgeBacked is not null)
        {
            _delivery.EditQueued(paths);
        }
    }

    private void AfterEditPurge(string[] sent, CdnInvalidationResult result, CdnPurgeCause cause)
    {
        if (ActivePurgeBacked is not { } purgeBacked)
        {
            return;
        }

        if (result.Outcome is not CdnInvalidationOutcome.Failed)
        {
            _delivery.Ended(sent);
            if (result.Outcome is CdnInvalidationOutcome.Submitted)
            {
                AfterSubmitted(sent, cause);
            }

            return;
        }

        var plan = _delivery.Failed(sent, purgeBacked.LongestSharedLifetime);
        if (plan.Abandoned > 0)
        {
            Log.FailedEditPurgeAbandoned(logger, plan.Abandoned);
        }

        if (plan.Paths.Length > 0)
        {
            DeferToNextWindow(plan.Paths, plan.Delay, cause);
        }
    }

    // A settle repeat is itself never repeated.
    private void AfterSubmitted(string[] sent, CdnPurgeCause cause)
    {
        if (cause.Kind is CdnPurgeKind.Settle || ActivePurgeBacked is null)
        {
            return;
        }

        // Checked and scheduled under the lock the flush takes, so a settle is either drained by it or logged.
        bool stopping;
        DateTimeOffset? due = null;
        lock (_gate)
        {
            stopping = _stopping;
            if (!stopping)
            {
                due = _delivery.ScheduleSettle(sent, cause);
            }
        }

        if (stopping)
        {
            Log.ShutdownPurgesNotSent(logger, sent.Length, string.Join(' ', sent));
        }
        else if (due is { } settleAt)
        {
            _ = SettleWhenDueAsync(settleAt);
        }
    }

    private async Task SettleWhenDueAsync(DateTimeOffset due)
    {
        if (!await WaitUntilDueOrShutdownAsync(due - _timeProvider.GetUtcNow()).ConfigureAwait(false))
        {
            return;
        }

        var batch = _delivery.TakeDueSettles(due);
        if (batch.Next is { } next && !_stopping)
        {
            _ = SettleWhenDueAsync(next);
        }

        foreach (var repeat in batch.Batches)
        {
            if (_stopping || _applicationStopping.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = await SendAfterEditAsync(repeat.Paths, repeat.Cause, _applicationStopping)
                    .ConfigureAwait(false);
                if (result is { Outcome: CdnInvalidationOutcome.Failed })
                {
                    Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
                }
            }
            catch (OperationCanceledException) when (_stopping || _applicationStopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log.BackgroundPurgeFailed(logger, ex);
                _reliability?.RecordFailedPurge();
            }
        }
    }
}

// Sends this task's waiting purges when the host stops: a scale-in or a replacement must not lose them. The flush has
// its own short deadline rather than the host's token, which may be nearly spent by the services stopped before it.
internal sealed class CdnPendingPurgeFlush(CdnInvalidationService invalidation) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => invalidation.FlushPendingAsync();
}
