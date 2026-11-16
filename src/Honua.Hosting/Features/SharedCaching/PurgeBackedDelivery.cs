// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.SharedCaching;

// The state purge-backed lifetimes add to a task's purges: which paths failed and since when, and which paths wait
// for their settle repeat. CdnInvalidationService decides when to send; this decides what and how long to wait.
internal sealed class PurgeBackedDelivery(TimeProvider clock)
{
    internal static readonly TimeSpan FirstRetryDelay = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromHours(1);

    // Longer than a slow render, so the repeat lands after any response read before the change committed.
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, FailedPath> _failed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ScheduledPurge> _settleAfter = new(StringComparer.Ordinal);
    private DateTimeOffset? _settleDueAt;

    // 5, 10, 20, 40 minutes, then hourly.
    internal static TimeSpan RetryDelay(int attempts)
    {
        var delay = FirstRetryDelay * (1 << Math.Clamp(attempts - 1, 0, 4));
        return delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    // An edit restarts a failing path's give-up clock, since copies stored after it live a full lifetime from then.
    internal void EditQueued(IEnumerable<string> paths)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (_failed.TryGetValue(path, out var failed))
                {
                    _failed[path] = failed with { Since = now };
                }
            }
        }
    }

    // Which of the failed paths to send again, and when. A path is given up once every copy it should have removed
    // has expired.
    internal RetryPlan Failed(IReadOnlyCollection<string> sent, TimeSpan giveUpAfter)
    {
        var now = clock.GetUtcNow();
        var retry = new List<string>(sent.Count);
        var delay = MaximumRetryDelay;
        var abandoned = 0;
        lock (_gate)
        {
            foreach (var path in sent)
            {
                var failed = _failed.TryGetValue(path, out var known) ? known with { Attempts = known.Attempts + 1 } : new FailedPath(now, 1);
                if (now - failed.Since >= giveUpAfter)
                {
                    _failed.Remove(path);
                    abandoned++;
                    continue;
                }

                _failed[path] = failed;
                retry.Add(path);
                delay = RetryDelay(failed.Attempts) < delay ? RetryDelay(failed.Attempts) : delay;
            }
        }

        return new RetryPlan([.. retry], delay, abandoned);
    }

    // Any outcome but a failure ends a path's retries: sent, refused for budget, or handed back for a busy CDN.
    internal void Ended(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            foreach (var path in paths)
            {
                _failed.Remove(path);
            }
        }
    }

    // Returns the time a new settle timer must fire, or null when one is already waiting or nothing needs to settle.
    internal DateTimeOffset? ScheduleSettle(IEnumerable<string> sent, CdnPurgeCause? cause = null)
    {
        var due = clock.GetUtcNow() + SettleDelay;
        var repeat = (cause ?? CdnPurgeCause.Edit("settle")).AsSettle();
        lock (_gate)
        {
            foreach (var path in sent)
            {
                // A later edit postpones the repeat, but cannot remove an earlier exemption.
                var stated = repeat.Reason == "edit" && _settleAfter.TryGetValue(path, out var pending) && pending.Cause.Reason != "edit"
                    ? pending.Cause : repeat;
                _settleAfter[path] = new ScheduledPurge(due, stated);
            }

            if (_settleDueAt is not null || _settleAfter.Count == 0)
            {
                return null;
            }

            _settleDueAt = due;
            return due;
        }
    }

    // The paths whose settle delay has passed, and when the timer must fire next for the rest. A timer that is no
    // longer the scheduled one gets nothing.
    internal SettleBatch TakeDueSettles(DateTimeOffset timer)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_settleDueAt != timer)
            {
                return new SettleBatch([], null);
            }

            string[] due = [.. _settleAfter.Where(entry => entry.Value.Due <= now).Select(entry => entry.Key)];
            var batches = PurgeBatch.Group(due.Select(path => new KeyValuePair<string, CdnPurgeCause>(path, _settleAfter[path].Cause)));
            foreach (var path in due)
            {
                _settleAfter.Remove(path);
            }

            _settleDueAt = _settleAfter.Count > 0 ? _settleAfter.Values.Min(value => value.Due) : null;
            return new SettleBatch(batches, _settleDueAt);
        }
    }

    // Everything still waiting to settle, for a stopping task; retries are already in the coalescing window.
    internal PurgeBatch[] TakeAll()
    {
        lock (_gate)
        {
            var batches = PurgeBatch.Group(_settleAfter.Select(entry => new KeyValuePair<string, CdnPurgeCause>(entry.Key, entry.Value.Cause)));
            _settleAfter.Clear();
            _failed.Clear();
            _settleDueAt = null;
            return batches;
        }
    }

    private readonly record struct ScheduledPurge(DateTimeOffset Due, CdnPurgeCause Cause);

    private readonly record struct FailedPath(DateTimeOffset Since, int Attempts);
}

internal readonly record struct RetryPlan(string[] Paths, TimeSpan Delay, int Abandoned);

internal readonly record struct SettleBatch(PurgeBatch[] Batches, DateTimeOffset? Next)
{
    internal string[] Due => [.. Batches.SelectMany(batch => batch.Paths)];
}

internal readonly record struct PurgeBatch(string[] Paths, CdnPurgeCause Cause)
{
    internal static PurgeBatch[] Group(IEnumerable<KeyValuePair<string, CdnPurgeCause>> paths)
        => [.. paths.GroupBy(entry => entry.Value)
            .Select(group => new PurgeBatch([.. group.Select(entry => entry.Key)], group.Key))];
}
