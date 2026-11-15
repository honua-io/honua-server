// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Honua.Infrastructure.SharedCaching;

// What purge-backed lifetimes add to edit purges, where a lost purge would leave a layer stale for the long lifetime
// rather than minutes. A failed purge is retried with backoff until every copy it should have removed has expired; a
// sent one is repeated once after a settle delay, for a response rendered before the edit committed that reached the
// CDN after the first purge; and purges still waiting when the task stops are sent then.
internal sealed partial class CdnInvalidationService
{
    internal static readonly TimeSpan FailedEditPurgeRetryDelay = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan MaximumFailedEditPurgeRetryDelay = TimeSpan.FromHours(1);

    // Longer than a slow render, so the repeat lands after any response read before the commit.
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(1);

    private readonly Dictionary<string, DateTimeOffset> _failedSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failedAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _settleAfter = new(StringComparer.Ordinal);
    private DateTimeOffset? _settleDueAt;

    // Sends every edit purge this task still holds: pending in a window, waiting for a retry, or waiting to settle.
    // Called when the task stops; leases are not claimed, since a purge sent twice costs less than one lost.
    internal async Task FlushPendingAsync(CancellationToken cancellationToken)
    {
        string[] paths;
        lock (_gate)
        {
            paths = [.. _pendingEditPaths.Union(_settleAfter.Keys, StringComparer.Ordinal)];
            _pendingEditPaths.Clear();
            _settleAfter.Clear();
            _failedSince.Clear();
            _failedAttempts.Clear();
            _windowClosesAt = null;
            _settleDueAt = null;
        }

        if (paths.Length == 0 || !CdnConfigured)
        {
            return;
        }

        var batches = await SendWithinLimitsAsync(paths, CdnPurgeCause.Edit(CdnPurgeCause.Shutdown), cancellationToken)
            .ConfigureAwait(false);
        if (batches.Result.Failed)
        {
            Log.EditInvalidationFailed(logger, batches.Result.Detail ?? batches.Result.Outcome.ToString());
        }
    }

    private void AfterEditPurge(string[] sent, BatchInvalidationResult batches, CdnPurgeCause cause)
    {
        if (batches.BusyPaths.Count > 0)
        {
            _reliability?.RecordFailedPurge();
        }

        if (_options?.CurrentValue.PurgeBacked is not { Configured: true } purgeBacked)
        {
            return;
        }

        switch (batches.Result.Outcome)
        {
            case CdnInvalidationOutcome.Failed:
                RetryFailed(sent, purgeBacked.LongestSharedLifetime);
                break;
            case CdnInvalidationOutcome.Submitted:
                ForgetFailures(sent);
                if (cause.ChangeId != CdnPurgeCause.SettledEdits)
                {
                    ScheduleSettle(sent);
                }

                break;
        }
    }

    private void RetryFailed(string[] sent, TimeSpan giveUpAfter)
    {
        var now = _timeProvider.GetUtcNow();
        var retry = new List<string>(sent.Length);
        var delay = MaximumFailedEditPurgeRetryDelay;
        var abandoned = 0;
        lock (_gate)
        {
            foreach (var path in sent)
            {
                var since = _failedSince.TryGetValue(path, out var first) ? first : now;
                if (now - since >= giveUpAfter)
                {
                    _failedSince.Remove(path);
                    _failedAttempts.Remove(path);
                    abandoned++;
                    continue;
                }

                var attempts = _failedAttempts.GetValueOrDefault(path) + 1;
                _failedSince[path] = since;
                _failedAttempts[path] = attempts;
                retry.Add(path);
                delay = Min(delay, RetryDelay(attempts));
            }
        }

        if (abandoned > 0)
        {
            Log.FailedEditPurgeAbandoned(logger, abandoned);
        }

        if (retry.Count > 0)
        {
            DeferToNextWindow(retry, delay);
        }
    }

    // 5, 10, 20, 40 minutes, then hourly.
    internal static TimeSpan RetryDelay(int attempts)
        => Min(FailedEditPurgeRetryDelay * (1 << Math.Clamp(attempts - 1, 0, 4)), MaximumFailedEditPurgeRetryDelay);

    private void ForgetFailures(string[] sent)
    {
        lock (_gate)
        {
            foreach (var path in sent)
            {
                _failedSince.Remove(path);
                _failedAttempts.Remove(path);
            }
        }
    }

    private void ScheduleSettle(string[] sent)
    {
        var due = _timeProvider.GetUtcNow() + SettleDelay;
        lock (_gate)
        {
            foreach (var path in sent)
            {
                _settleAfter[path] = due;
            }

            if (_settleDueAt is not null)
            {
                return;
            }

            _settleDueAt = due;
        }

        _ = SettleWhenDueAsync(due);
    }

    private async Task SettleWhenDueAsync(DateTimeOffset due)
    {
        var delay = due - _timeProvider.GetUtcNow();
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider).ConfigureAwait(false);
        }

        string[] paths;
        DateTimeOffset? next = null;
        lock (_gate)
        {
            if (_settleDueAt != due)
            {
                return;
            }

            // A path purged again since it was scheduled waits for its own settle delay.
            var now = _timeProvider.GetUtcNow();
            paths = [.. _settleAfter.Where(entry => entry.Value <= now).Select(entry => entry.Key)];
            foreach (var path in paths)
            {
                _settleAfter.Remove(path);
            }

            _settleDueAt = next = _settleAfter.Count > 0 ? _settleAfter.Values.Min() : null;
        }

        if (next is { } later)
        {
            _ = SettleWhenDueAsync(later);
        }

        if (paths.Length > 0)
        {
            var result = await SendAfterEditAsync(paths, CdnPurgeCause.Edit(CdnPurgeCause.SettledEdits), CancellationToken.None)
                .ConfigureAwait(false);
            if (result is { Outcome: CdnInvalidationOutcome.Failed })
            {
                Log.EditInvalidationFailed(logger, result.Detail ?? "unknown");
            }
        }
    }

    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;
}

// Sends this task's waiting edit purges when the host stops: a scale-in or a replacement must not lose them.
internal sealed class CdnPendingPurgeFlush(CdnInvalidationService invalidation) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => invalidation.FlushPendingAsync(cancellationToken);
}
