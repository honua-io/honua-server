// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Honua.ServiceDefaults;

/// <summary>
/// In-process, bounded-memory rolling aggregation of serving-plane request latency,
/// partitioned by GIS protocol. Each observed protocol keeps a fixed-size ring reservoir
/// of the most recent duration samples tagged with a monotonic timestamp; a read computes
/// per-protocol p50/p95/p99 plus request/error counts over the configured window.
/// </summary>
/// <remarks>
/// The hot path (<see cref="Record"/>) is allocation-free after the first sample per
/// protocol: it takes a short per-protocol lock and writes three primitives into
/// pre-allocated arrays. Memory is bounded by <c>samplesPerProtocol × observedProtocols</c>;
/// under sustained throughput above the ring capacity the effective window shrinks to the
/// most recent <c>samplesPerProtocol</c> samples, so the reported count reflects the actual
/// number of in-window samples retained rather than total traffic. Timestamps are
/// <see cref="Stopwatch"/> ticks (monotonic, unaffected by wall-clock changes); the
/// timestamp source is injectable for deterministic testing.
/// </remarks>
public sealed class ServingLatencyAggregator
{
    private readonly ConcurrentDictionary<string, Reservoir> _reservoirs = new(StringComparer.Ordinal);
    private readonly int _samplesPerProtocol;
    private readonly long _windowTicks;
    private readonly TimeSpan _window;
    private readonly Func<long> _timestamp;

    /// <summary>
    /// Serving-plane HTTP status floor that classifies a request as a server error for the
    /// window error rate. Client errors (4xx) are counted as requests but not errors.
    /// </summary>
    private const int ServerErrorFloor = 500;

    /// <summary>
    /// Initializes a new instance of the <see cref="ServingLatencyAggregator"/> class.
    /// </summary>
    /// <param name="window">The rolling window length. Samples older than this are excluded from snapshots.</param>
    /// <param name="samplesPerProtocol">The fixed reservoir capacity per protocol (bounds memory).</param>
    /// <param name="timestampProvider">
    /// Optional monotonic timestamp source in <see cref="Stopwatch"/>-tick units; defaults to
    /// <see cref="Stopwatch.GetTimestamp"/>. Supplied by tests to simulate window expiry.
    /// </param>
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

    /// <summary>
    /// Records a completed serving-plane request. No-op for a blank protocol or negative duration.
    /// </summary>
    /// <param name="protocol">The GIS protocol family (bounded cardinality from the fixed protocol set).</param>
    /// <param name="durationMs">The elapsed request duration in milliseconds.</param>
    /// <param name="statusCode">The HTTP status code; <c>&gt;= 500</c> counts toward the error rate.</param>
    public void Record(string protocol, double durationMs, int statusCode)
    {
        if (string.IsNullOrWhiteSpace(protocol) || durationMs < 0 || double.IsNaN(durationMs))
        {
            return;
        }

        var reservoir = _reservoirs.GetOrAdd(protocol, static (_, capacity) => new Reservoir(capacity), _samplesPerProtocol);
        reservoir.Add(_timestamp(), durationMs, statusCode >= ServerErrorFloor);
    }

    /// <summary>
    /// Computes a point-in-time snapshot of per-protocol latency percentiles and counts over the window.
    /// </summary>
    /// <returns>The serving latency snapshot; protocol entries are ordered by protocol name.</returns>
    public ServingLatencySnapshot GetSnapshot()
    {
        var now = _timestamp();
        var minTimestamp = now - _windowTicks;

        var protocols = new List<ServingLatencyProtocolSnapshot>(_reservoirs.Count);
        foreach (var pair in _reservoirs)
        {
            var (durations, errorCount) = pair.Value.CopyInWindow(minTimestamp);
            if (durations.Length == 0)
            {
                continue;
            }

            Array.Sort(durations);
            protocols.Add(new ServingLatencyProtocolSnapshot
            {
                Protocol = pair.Key,
                RequestCount = durations.Length,
                ErrorCount = errorCount,
                ErrorRate = (double)errorCount / durations.Length,
                P50Ms = Percentile(durations, 50),
                P95Ms = Percentile(durations, 95),
                P99Ms = Percentile(durations, 99),
                MaxMs = durations[^1],
            });
        }

        protocols.Sort(static (a, b) => string.CompareOrdinal(a.Protocol, b.Protocol));

        return new ServingLatencySnapshot
        {
            WindowSeconds = _window.TotalSeconds,
            GeneratedAt = DateTimeOffset.UtcNow,
            Protocols = protocols,
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
            }
        }

        public (double[] Durations, long ErrorCount) CopyInWindow(long minTimestamp)
        {
            lock (_gate)
            {
                if (_count == 0)
                {
                    return (Array.Empty<double>(), 0);
                }

                var durations = new double[_count];
                var written = 0;
                long errorCount = 0;
                for (var i = 0; i < _count; i++)
                {
                    if (_timestamps[i] < minTimestamp)
                    {
                        continue;
                    }

                    durations[written++] = _durations[i];
                    if (_errors[i])
                    {
                        errorCount++;
                    }
                }

                if (written == durations.Length)
                {
                    return (durations, errorCount);
                }

                var trimmed = new double[written];
                Array.Copy(durations, trimmed, written);
                return (trimmed, errorCount);
            }
        }
    }
}

/// <summary>
/// Point-in-time snapshot of serving-plane latency, aggregated per protocol over a rolling window.
/// </summary>
public sealed record ServingLatencySnapshot
{
    /// <summary>Gets the rolling window length, in seconds, over which the percentiles are computed.</summary>
    public required double WindowSeconds { get; init; }

    /// <summary>Gets the UTC timestamp at which the snapshot was produced.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }

    /// <summary>Gets the per-protocol latency entries (only protocols with in-window samples), ordered by protocol name.</summary>
    public required IReadOnlyList<ServingLatencyProtocolSnapshot> Protocols { get; init; }
}

/// <summary>
/// Per-protocol serving latency percentiles and request/error counts over the aggregation window.
/// </summary>
public sealed record ServingLatencyProtocolSnapshot
{
    /// <summary>Gets the GIS protocol family this entry describes.</summary>
    public required string Protocol { get; init; }

    /// <summary>Gets the number of in-window samples retained for this protocol.</summary>
    public required long RequestCount { get; init; }

    /// <summary>Gets the number of retained samples that were server errors (HTTP status &gt;= 500).</summary>
    public required long ErrorCount { get; init; }

    /// <summary>Gets the server-error rate over the retained samples (0.0 to 1.0).</summary>
    public required double ErrorRate { get; init; }

    /// <summary>Gets the 50th-percentile (median) request duration in milliseconds.</summary>
    public required double P50Ms { get; init; }

    /// <summary>Gets the 95th-percentile request duration in milliseconds.</summary>
    public required double P95Ms { get; init; }

    /// <summary>Gets the 99th-percentile request duration in milliseconds.</summary>
    public required double P99Ms { get; init; }

    /// <summary>Gets the maximum retained request duration in milliseconds.</summary>
    public required double MaxMs { get; init; }
}
