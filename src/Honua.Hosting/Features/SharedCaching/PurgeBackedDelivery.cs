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
    private readonly Dictionary<string, DateTimeOffset> _settleAfter = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settleOnly = new(StringComparer.Ordinal);
    private DateTimeOffset? _settleDueAt;

    // 5, 10, 20, 40 minutes, then hourly.
    internal static TimeSpan RetryDelay(int attempts)
    {
        var delay = FirstRetryDelay * (1 << Math.Clamp(attempts - 1, 0, 4));
        return delay < MaximumRetryDelay ? delay : MaximumRetryDelay;
    }

    // An edit restarts a failing path's give-up clock, since copies stored after it live a full lifetime from then, and
    // turns a path held only for its settle repeat back into an ordinary one.
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

                _settleOnly.Remove(path);
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

    // Paths left over from a settle repeat (busy CDN) are still sent, but do not settle again.
    internal void SettleOnly(IEnumerable<string> paths)
    {
        lock (_gate)
        {
            _settleOnly.UnionWith(paths);
        }
    }

    // Returns the time a new settle timer must fire, or null when one is already waiting or nothing needs to settle.
    internal DateTimeOffset? ScheduleSettle(IEnumerable<string> sent)
    {
        var due = clock.GetUtcNow() + SettleDelay;
        lock (_gate)
        {
            foreach (var path in sent)
            {
                if (!_settleOnly.Remove(path))
                {
                    _settleAfter[path] = due;
                }
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

            string[] due = [.. _settleAfter.Where(entry => entry.Value <= now).Select(entry => entry.Key)];
            foreach (var path in due)
            {
                _settleAfter.Remove(path);
            }

            _settleDueAt = _settleAfter.Count > 0 ? _settleAfter.Values.Min() : null;
            return new SettleBatch(due, _settleDueAt);
        }
    }

    // Everything still waiting to settle, for a stopping task; retries are already in the coalescing window.
    internal string[] TakeAll()
    {
        lock (_gate)
        {
            string[] paths = [.. _settleAfter.Keys];
            _settleAfter.Clear();
            _settleOnly.Clear();
            _failed.Clear();
            _settleDueAt = null;
            return paths;
        }
    }

    private readonly record struct FailedPath(DateTimeOffset Since, int Attempts);
}

internal readonly record struct RetryPlan(string[] Paths, TimeSpan Delay, int Abandoned);

internal readonly record struct SettleBatch(string[] Due, DateTimeOffset? Next);
