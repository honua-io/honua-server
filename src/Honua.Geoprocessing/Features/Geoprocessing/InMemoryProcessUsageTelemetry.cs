// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Honua.Core.Features.Geoprocessing.Abstractions;

namespace Honua.Geoprocessing;

/// <summary>
/// Process-local, in-memory <see cref="IProcessUsageTelemetry"/> implementation
/// (#2144). Backs the usage-ranked GP tool tiering view with a real, queryable
/// store (not a stub): every <see cref="RecordInvocation"/> updates a concurrent
/// per-process counter, and <see cref="GetRanking"/> projects a stably-ordered
/// snapshot that reflects everything recorded before the read. Recording is a
/// <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd(TKey, Func{TKey, TValue})"/>
/// plus a few <see cref="Interlocked"/> increments, so it adds no measurable
/// hot-path overhead to job execution.
/// </summary>
internal sealed class InMemoryProcessUsageTelemetry(TimeProvider timeProvider) : IProcessUsageTelemetry
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void RecordInvocation(string processId, bool succeeded)
    {
        if (string.IsNullOrWhiteSpace(processId))
        {
            return;
        }

        var counter = _counters.GetOrAdd(processId, static _ => new Counter());
        counter.Record(succeeded, timeProvider.GetUtcNow());
    }

    /// <inheritdoc />
    public IReadOnlyList<ProcessUsageRanking> GetRanking(int? limit = null)
    {
        var ranked = _counters
            .Select(kvp => kvp.Value.Snapshot(kvp.Key))
            .OrderByDescending(r => r.InvocationCount)
            .ThenByDescending(r => r.LastInvokedAt)
            .ThenBy(r => r.ProcessId, StringComparer.Ordinal)
            .ToList();

        if (limit is > 0 && limit.Value < ranked.Count)
        {
            ranked.RemoveRange(limit.Value, ranked.Count - limit.Value);
        }

        return ranked;
    }

    private sealed class Counter
    {
        private long _total;
        private long _success;
        private long _failure;
        private long _lastInvokedTicks;

        public void Record(bool succeeded, DateTimeOffset at)
        {
            Interlocked.Increment(ref _total);
            if (succeeded)
            {
                Interlocked.Increment(ref _success);
            }
            else
            {
                Interlocked.Increment(ref _failure);
            }

            // Monotonic last-write-wins on the UTC tick stamp.
            var ticks = at.UtcTicks;
            long observed;
            do
            {
                observed = Interlocked.Read(ref _lastInvokedTicks);
                if (ticks <= observed)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(ref _lastInvokedTicks, ticks, observed) != observed);
        }

        public ProcessUsageRanking Snapshot(string processId)
        {
            var ticks = Interlocked.Read(ref _lastInvokedTicks);
            return new ProcessUsageRanking(
                processId,
                Interlocked.Read(ref _total),
                Interlocked.Read(ref _success),
                Interlocked.Read(ref _failure),
                new DateTimeOffset(ticks, TimeSpan.Zero));
        }
    }
}
