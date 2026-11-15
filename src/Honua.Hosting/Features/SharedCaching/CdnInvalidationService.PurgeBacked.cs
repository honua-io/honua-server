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
        string[] paths;
        lock (_gate)
        {
            _stopping = true;
            paths = [.. _pendingEditPaths.Union(_delivery.TakeAll(), StringComparer.Ordinal)];
            _pendingEditPaths.Clear();
            _windowClosesAt = null;
        }

        if (paths.Length == 0 || !CdnConfigured)
        {
            return;
        }

        using var deadline = new CancellationTokenSource(ShutdownFlushTimeout);
        try
        {
            var batches = await SendWithinLimitsAsync(paths, CdnPurgeCause.Shutdown(), deadline.Token)
                .ConfigureAwait(false);
            if (batches.Result.Failed)
            {
                Log.ShutdownPurgesNotSent(logger, paths.Length, string.Join(' ', paths));
            }
        }
        catch (OperationCanceledException)
        {
            Log.ShutdownPurgesNotSent(logger, paths.Length, string.Join(' ', paths));
        }
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
            DeferToNextWindow(plan.Paths, plan.Delay);
        }
    }

    // A settle repeat is itself never repeated.
    private void AfterSubmitted(IReadOnlyCollection<string> sent, CdnPurgeCause cause)
    {
        if (cause.Kind is CdnPurgeKind.Settle || ActivePurgeBacked is null)
        {
            return;
        }

        if (_stopping)
        {
            Log.ShutdownPurgesNotSent(logger, sent.Count, string.Join(' ', sent));
            return;
        }

        if (_delivery.ScheduleSettle(sent) is { } due)
        {
            _ = SettleWhenDueAsync(due);
        }
    }

    private async Task SettleWhenDueAsync(DateTimeOffset due)
    {
        var delay = due - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider).ConfigureAwait(false);
        }

        var batch = _delivery.TakeDueSettles(due);
        if (batch.Next is { } next)
        {
            _ = SettleWhenDueAsync(next);
        }

        if (batch.Due.Length > 0)
        {
            var result = await SendAfterEditAsync(batch.Due, CdnPurgeCause.Settle(), CancellationToken.None)
                .ConfigureAwait(false);
            if (result is { Outcome: CdnInvalidationOutcome.Failed })
            {
                Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
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
