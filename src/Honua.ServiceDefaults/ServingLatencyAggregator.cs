// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Honua.Core.Features.Observability.Domain;

namespace Honua.ServiceDefaults;

public sealed class ServingLatencyAggregator
{
    private readonly ConcurrentDictionary<string, Reservoir> _reservoirs = new(StringComparer.Ordinal);
    private readonly int _samplesPerProtocol;
    private readonly long _windowTicks;
    private readonly TimeSpan _window;
    private readonly Func<long> _timestamp;

    private const int ServerErrorFloor = 500;

    public ServingLatencyAggregator(TimeSpan window, int samplesPerProtocol, Func<long>? timestampProvider = null)
    {
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");
        }

        if (samplesPerProtocol <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samplesPerProtocol), samplesPerProtocol, "Sample capacity must be positive.");
        }

        _window = window;
        _samplesPerProtocol = samplesPerProtocol;
        _timestamp = timestampProvider ?? Stopwatch.GetTimestamp;
        _windowTicks = Math.Max(1L, (long)(window.TotalSeconds * Stopwatch.Frequency));
    }

    public void Record(string protocol, double durationMs, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(protocol) || durationMs < 0 || double.IsNaN(durationMs))
        {
            return;
        }

        var reservoir = _reservoirs.GetOrAdd(protocol, static (_, capacity) => new Reservoir(capacity), _samplesPerProtocol);
        reservoir.Add(_timestamp(), durationMs, statusCode >= ServerErrorFloor);
    }

    public ServingLatencySnapshot GetSnapshot()
    {
        var now = _timestamp();
        var minTimestamp = now - _windowTicks;

        var protocols = new List<ServingLatencyProtocolSnapshot>(_reservoirs.Count);
        var idle = new List<ServingLatencyIdleProtocol>();
        foreach (var pair in _reservoirs)
        {
            var retained = pair.Value.CopyInWindow(minTimestamp);
            var durations = retained.Durations;
            if (durations.Length == 0)
            {
                // A protocol that served traffic and then idled past the window has no retained
                // samples but is NOT a freshly reset process. Its lifetime counters are published
                // on a separate list so the node-local diagnostic can tell the two apart, while
                // latency consumers keep iterating Protocols over populations that actually exist.
                if (retained.TotalRecorded > 0)
                {
                    idle.Add(new ServingLatencyIdleProtocol
                    {
                        Protocol = pair.Key,
                        RetentionCapacity = _samplesPerProtocol,
                        TotalRecordedSinceReset = retained.TotalRecorded,
                        OverwrittenSampleCount = Math.Max(0, retained.TotalRecorded - _samplesPerProtocol),
                    });
                }

                continue;
            }

            Array.Sort(durations);
            protocols.Add(new ServingLatencyProtocolSnapshot
            {
                Protocol = pair.Key,
                RequestCount = durations.Length,
                ErrorCount = retained.ErrorCount,
                ErrorRate = (double)retained.ErrorCount / durations.Length,
                P50Ms = Percentile(durations, 50),
                P95Ms = Percentile(durations, 95),
                P99Ms = Percentile(durations, 99),
                MaxMs = durations[^1],
                RetentionCapacity = _samplesPerProtocol,
                TotalRecordedSinceReset = retained.TotalRecorded,
                OverwrittenSampleCount = Math.Max(0, retained.TotalRecorded - _samplesPerProtocol),
                OldestRetainedSampleAgeSeconds = Math.Max(0, (double)(now - retained.OldestTimestamp) / Stopwatch.Frequency),
                NewestRetainedSampleAgeSeconds = Math.Max(0, (double)(now - retained.NewestTimestamp) / Stopwatch.Frequency),
                // Mergeable distribution sketch (#2809): lets cluster-wide percentiles be recomputed from
                // summed per-replica distributions instead of a mean of percentiles that hides a sick replica.
                Distribution = LatencyDistribution.FromDurations(durations),
            });
        }

        protocols.Sort(static (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol));
        idle.Sort(static (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol));

        return new ServingLatencySnapshot
        {
            WindowSeconds = _window.TotalSeconds,
            GeneratedAt = DateTimeOffset.UtcNow,
            Protocols = protocols,
            IdleProtocols = idle,
        };
    }

    // Nearest-rank percentile over an ascending-sorted, non-empty sample set.
    private static double Percentile(double[] sortedAscending, double percentile)
    {
        var length = sortedAscending.Length;
        var rank = (int)Math.Ceiling(percentile / 100.0 * length);
        rank = Math.Clamp(rank, 1, length);
        return sortedAscending[rank - 1];
    }

    private sealed class Reservoir
    {
        private readonly object _gate = new();
        private readonly long[] _timestamps;
        private readonly double[] _durations;
        private readonly bool[] _errors;
        private int _next;
        private int _count;
        private long _totalRecorded;

        public Reservoir(int capacity)
        {
            _timestamps = new long[capacity];
            _durations = new double[capacity];
            _errors = new bool[capacity];
        }

        public void Add(long timestamp, double durationMs, bool isError)
        {
            lock (_gate)
            {
                _timestamps[_next] = timestamp;
                _durations[_next] = durationMs;
                _errors[_next] = isError;
                _next = (_next + 1) % _timestamps.Length;
                if (_count < _timestamps.Length)
                {
                    _count++;
                }

                _totalRecorded++;
            }
        }

        public ReservoirWindow CopyInWindow(long minTimestamp)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return new ReservoirWindow(Array.Empty<double>(), 0, _totalRecorded, 0, 0);
                }

                var durations = new double[_count];
                var written = 0;
                long errorCount = 0;
                var oldestTimestamp = long.MaxValue;
                var newestTimestamp = long.MinValue;
                for (var i = 0; i < _count; i++)
                {
                    if (_timestamps[i] < minTimestamp)
                    {
                        continue;
                    }

                    durations[written++] = _durations[i];
                    oldestTimestamp = Math.Min(oldestTimestamp, _timestamps[i]);
                    newestTimestamp = Math.Max(newestTimestamp, _timestamps[i]);
                    if (_errors[i])
                    {
                        errorCount++;
                    }
                }

                if (written == durations.Length)
                {
                    return new ReservoirWindow(
                        durations, errorCount, _totalRecorded, oldestTimestamp, newestTimestamp);
                }

                var trimmed = new double[written];
                Array.Copy(durations, trimmed, written);
                return new ReservoirWindow(
                    trimmed,
                    errorCount,
                    _totalRecorded,
                    written == 0 ? 0 : oldestTimestamp,
                    written == 0 ? 0 : newestTimestamp);
            }
        }
    }

    private readonly record struct ReservoirWindow(
        double[] Durations,
        long ErrorCount,
        long TotalRecorded,
        long OldestTimestamp,
        long NewestTimestamp);
}

public sealed record ServingLatencySnapshot
{
    public required double WindowSeconds { get; init; }

    public required DateTimeOffset GeneratedAt { get; init; }

    public required IReadOnlyList<ServingLatencyProtocolSnapshot> Protocols { get; init; }

    public IReadOnlyList<ServingLatencyIdleProtocol> IdleProtocols { get; init; } = [];
}

public sealed record ServingLatencyIdleProtocol
{
    public required string Protocol { get; init; }

    public required int RetentionCapacity { get; init; }

    public required long TotalRecordedSinceReset { get; init; }

    public required long OverwrittenSampleCount { get; init; }
}

public sealed record ServingLatencyProtocolSnapshot
{
    public required string Protocol { get; init; }

    public required long RequestCount { get; init; }

    public required long ErrorCount { get; init; }

    public required double ErrorRate { get; init; }

    public required double P50Ms { get; init; }

    public required double P95Ms { get; init; }

    public required double P99Ms { get; init; }

    public required double MaxMs { get; init; }

    public int RetentionCapacity { get; init; }

    public long TotalRecordedSinceReset { get; init; }

    public long OverwrittenSampleCount { get; init; }

    public double OldestRetainedSampleAgeSeconds { get; init; }

    public double NewestRetainedSampleAgeSeconds { get; init; }

    public LatencyDistribution? Distribution { get; init; }
}
