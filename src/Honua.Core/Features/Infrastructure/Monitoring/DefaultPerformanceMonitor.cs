// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Default implementation of <see cref="IPerformanceMonitor"/> using .NET Metrics API.
/// </summary>
/// <remarks>
/// This implementation provides comprehensive performance monitoring using the .NET Metrics API
/// and integrates with OpenTelemetry for telemetry export.
/// </remarks>
internal sealed class DefaultPerformanceMonitor : IPerformanceMonitor, ICacheMetricsSnapshotProvider
{
    private readonly ConcurrentDictionary<string, CacheOperationCounters> _cacheCounters = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public void RecordDatabaseQuery(string queryType, string layerId, TimeSpan duration, int recordCount)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("query_type", queryType),
            new("layer_id", layerId)
        };

        PerformanceMetrics.DatabaseQueryDuration.Record(duration.TotalMilliseconds, tags);
        PerformanceMetrics.DatabaseQueryCount.Add(1, tags);
        PerformanceMetrics.DatabaseQueryRecordCount.Record(recordCount, tags);
    }

    /// <inheritdoc />
    public void RecordHttpRequest(string method, string endpoint, int statusCode, TimeSpan duration)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("method", method),
            new("endpoint", endpoint),
            new("status_code", statusCode.ToString(CultureInfo.InvariantCulture))
        };

        PerformanceMetrics.HttpRequestDuration.Record(duration.TotalMilliseconds, tags);
        PerformanceMetrics.HttpRequestCount.Add(1, tags);
    }

    /// <inheritdoc />
    public void RecordActiveHttpRequestDelta(int delta)
    {
        PerformanceMetrics.ActiveHttpRequests.Add(delta);
    }

    /// <inheritdoc />
    public void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections)
    {
        // Record GC metrics for each generation
        var gen0Tags = new KeyValuePair<string, object?>[] { new("generation", "0") };
        var gen1Tags = new KeyValuePair<string, object?>[] { new("generation", "1") };
        var gen2Tags = new KeyValuePair<string, object?>[] { new("generation", "2") };

        PerformanceMetrics.GarbageCollectionCount.Add(gen0Collections, gen0Tags);
        PerformanceMetrics.GarbageCollectionCount.Add(gen1Collections, gen1Tags);
        PerformanceMetrics.GarbageCollectionCount.Add(gen2Collections, gen2Tags);
    }

    /// <inheritdoc />
    public void RecordCacheMetrics(string cacheType, string operation)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("cache_type", cacheType),
            new("operation", operation)
        };

        PerformanceMetrics.CacheOperationCount.Add(1, tags);

        var normalizedType = string.IsNullOrWhiteSpace(cacheType) ? "unknown" : cacheType;
        var normalizedOperation = string.IsNullOrWhiteSpace(operation) ? "unknown" : operation;
        var counters = _cacheCounters.GetOrAdd(normalizedType, _ => new CacheOperationCounters());
        counters.Record(normalizedOperation);
    }

    /// <inheritdoc />
    public CacheMetricsSnapshot GetCacheMetricsSnapshot()
    {
        var types = new Dictionary<string, CacheTypeMetricsSnapshot>(StringComparer.OrdinalIgnoreCase);
        long totalHits = 0;
        long totalMisses = 0;
        long totalEvictions = 0;

        foreach (var (cacheType, counters) in _cacheCounters)
        {
            var snapshot = counters.Snapshot();
            types[cacheType] = snapshot;
            totalHits += snapshot.Hits;
            totalMisses += snapshot.Misses;
            totalEvictions += snapshot.Evictions;
        }

        return new CacheMetricsSnapshot
        {
            TotalHits = totalHits,
            TotalMisses = totalMisses,
            TotalEvictions = totalEvictions,
            Types = types
        };
    }

    /// <inheritdoc />
    public IOperationScope StartOperation(string operationName)
    {
        return new OperationScope(operationName);
    }

    /// <inheritdoc />
    public void RecordCounter(string name, long value, IDictionary<string, string>? tags = null)
    {
        var tagPairs = ConvertTags(tags);
        var counter = PerformanceMetrics.CreateCounter(name);
        counter.Add(value, tagPairs);
    }

    /// <inheritdoc />
    public void RecordHistogram(string name, double value, IDictionary<string, string>? tags = null)
    {
        var tagPairs = ConvertTags(tags);
        var histogram = PerformanceMetrics.CreateHistogram(name);
        histogram.Record(value, tagPairs);
    }

    /// <summary>
    /// Converts a dictionary of tags to KeyValuePair array for metrics.
    /// </summary>
    /// <param name="tags">Tags dictionary</param>
    /// <returns>Array of key-value pairs</returns>
    private static KeyValuePair<string, object?>[] ConvertTags(IDictionary<string, string>? tags)
    {
        if (tags == null || tags.Count == 0)
        {
            return Array.Empty<KeyValuePair<string, object?>>();
        }

        return tags.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)).ToArray();
    }

    /// <summary>
    /// Internal implementation of operation scope for measuring operation duration.
    /// </summary>
    private sealed class OperationScope : IOperationScope
    {
        private readonly string _operationName;
        private readonly Stopwatch _stopwatch;
        private readonly Dictionary<string, string> _tags;

        public OperationScope(string operationName)
        {
            _operationName = operationName;
            _tags = new Dictionary<string, string>();
            _stopwatch = Stopwatch.StartNew();
        }

        /// <inheritdoc />
        public IOperationScope WithTag(string key, string value)
        {
            _tags[key] = value;
            return this;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _stopwatch.Stop();

            // Add operation name as a tag
            _tags["operation"] = _operationName;

            var tagPairs = _tags.Select(kvp => new KeyValuePair<string, object?>(kvp.Key, kvp.Value)).ToArray();

            // Record duration and count
            PerformanceMetrics.OperationDuration.Record(_stopwatch.Elapsed.TotalMilliseconds, tagPairs);
            PerformanceMetrics.OperationCount.Add(1, tagPairs);
        }
    }

    private sealed class CacheOperationCounters
    {
        private long _hits;
        private long _misses;
        private long _evictions;

        public void Record(string operation)
        {
            switch (operation.ToLowerInvariant())
            {
                case "hit":
                    Interlocked.Increment(ref _hits);
                    break;
                case "miss":
                    Interlocked.Increment(ref _misses);
                    break;
                case "eviction":
                case "pattern_eviction":
                    Interlocked.Increment(ref _evictions);
                    break;
            }
        }

        public CacheTypeMetricsSnapshot Snapshot()
        {
            return new CacheTypeMetricsSnapshot
            {
                Hits = Interlocked.Read(ref _hits),
                Misses = Interlocked.Read(ref _misses),
                Evictions = Interlocked.Read(ref _evictions)
            };
        }
    }
}
