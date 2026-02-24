// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Default implementation of <see cref="IPerformanceMonitor"/> using .NET Metrics API.
/// </summary>
/// <remarks>
/// This implementation provides comprehensive performance monitoring using the .NET Metrics API
/// and integrates with OpenTelemetry for telemetry export.
/// </remarks>
internal sealed partial class DefaultPerformanceMonitor : IPerformanceMonitor, ICacheMetricsSnapshotProvider, IHttpRequestMetricsSnapshotProvider
{
    private readonly ConcurrentDictionary<string, CacheOperationCounters> _cacheCounters = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LatencyTracker> _operationLatencyTrackers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gcLock = new();
    private long _lastGen0Collections = -1;
    private long _lastGen1Collections = -1;
    private long _lastGen2Collections = -1;
    private readonly RequestMetricsAggregator _requestMetrics;
    private readonly MemoryPressureTracker _memoryTracker;
    private readonly ILogger<DefaultPerformanceMonitor>? _logger;

    public DefaultPerformanceMonitor(
        IOptions<PerformanceMonitoringOptions>? options = null,
        ILogger<DefaultPerformanceMonitor>? logger = null)
    {
        var slowThresholdMs = options?.Value.SlowRequestThreshold.TotalMilliseconds ?? 1000.0;
        _requestMetrics = new RequestMetricsAggregator(slowThresholdMs);
        _memoryTracker = new MemoryPressureTracker();
        _logger = logger;
    }

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
        _requestMetrics.RecordRequest(statusCode, duration.TotalMilliseconds);
    }

    /// <inheritdoc />
    public void RecordActiveHttpRequestDelta(int delta)
    {
        PerformanceMetrics.ActiveHttpRequests.Add(delta);
        _requestMetrics.RecordActiveDelta(delta);
    }

    /// <inheritdoc />
    public void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections)
    {
        // Record GC metrics for each generation
        var gen0Tags = new KeyValuePair<string, object?>[] { new("generation", "0") };
        var gen1Tags = new KeyValuePair<string, object?>[] { new("generation", "1") };
        var gen2Tags = new KeyValuePair<string, object?>[] { new("generation", "2") };

        long deltaGen0;
        long deltaGen1;
        long deltaGen2;

        lock (_gcLock)
        {
            if (_lastGen0Collections < 0)
            {
                _lastGen0Collections = gen0Collections;
                _lastGen1Collections = gen1Collections;
                _lastGen2Collections = gen2Collections;
                return;
            }

            deltaGen0 = Math.Max(0, gen0Collections - _lastGen0Collections);
            deltaGen1 = Math.Max(0, gen1Collections - _lastGen1Collections);
            deltaGen2 = Math.Max(0, gen2Collections - _lastGen2Collections);

            _lastGen0Collections = gen0Collections;
            _lastGen1Collections = gen1Collections;
            _lastGen2Collections = gen2Collections;
        }

        if (deltaGen0 > 0)
        {
            PerformanceMetrics.GarbageCollectionCount.Add(deltaGen0, gen0Tags);
        }

        if (deltaGen1 > 0)
        {
            PerformanceMetrics.GarbageCollectionCount.Add(deltaGen1, gen1Tags);
        }

        if (deltaGen2 > 0)
        {
            PerformanceMetrics.GarbageCollectionCount.Add(deltaGen2, gen2Tags);
        }
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
        var recordHitRatio = counters.Record(normalizedOperation);

        if (recordHitRatio)
        {
            var hitRatio = counters.GetHitRatio();
            var ratioTags = new KeyValuePair<string, object?>[]
            {
                new("cache_type", normalizedType)
            };
            PerformanceMetrics.CacheHitRatio.Record(hitRatio, ratioTags);
        }
    }

    /// <inheritdoc />
    public void RecordTransactionDuration(TimeSpan duration, int operationCount, bool wasCommitted)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("status", wasCommitted ? "committed" : "rolled_back"),
            new("operation_count", operationCount.ToString(CultureInfo.InvariantCulture))
        };

        PerformanceMetrics.TransactionDuration.Record(duration.TotalMilliseconds, tags);
        PerformanceMetrics.TransactionCount.Add(1, tags);
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
    public HttpRequestMetricsSnapshot GetHttpRequestMetricsSnapshot()
    {
        return _requestMetrics.Snapshot();
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

    /// <inheritdoc />
    public void RecordGeospatialOperation(string operationType, TimeSpan duration, int coordinateCount, int? fromSrid = null, int? toSrid = null)
    {
        var durationMs = duration.TotalMilliseconds;
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("operation_type", operationType),
            new("coordinate_count", coordinateCount.ToString(CultureInfo.InvariantCulture))
        };

        if (fromSrid.HasValue)
        {
            tags.Add(new("from_srid", fromSrid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (toSrid.HasValue)
        {
            tags.Add(new("to_srid", toSrid.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var tagArray = tags.ToArray();

        // Record to specific metrics based on operation type
        switch (operationType.ToLowerInvariant())
        {
            case "transform":
            case "coordinate_transform":
                PerformanceMetrics.CoordinateTransformDuration.Record(durationMs, tagArray);
                PerformanceMetrics.CoordinateTransformCount.Add(1, tagArray);
                break;

            case "spatial_query":
            case "intersects":
            case "contains":
            case "overlaps":
            case "within":
                PerformanceMetrics.SpatialQueryDuration.Record(durationMs, tagArray);
                PerformanceMetrics.SpatialQueryCount.Add(1, tagArray);
                break;

            case "spatial_filter":
            case "buffer":
            case "simplify":
                PerformanceMetrics.SpatialFilterDuration.Record(durationMs, tagArray);
                PerformanceMetrics.SpatialFilterCount.Add(1, tagArray);
                break;

            default:
                // Fall back to general geometry metrics
                PerformanceMetrics.GeometryOperationDuration.Record(durationMs, tagArray);
                PerformanceMetrics.GeometryOperationCount.Add(1, tagArray);
                PerformanceMetrics.GeometryComplexity.Record(coordinateCount, tagArray);
                break;
        }

        // Track latency percentiles
        var latencyKey = $"geospatial_{operationType}";
        var tracker = _operationLatencyTrackers.GetOrAdd(latencyKey, _ => new LatencyTracker());
        tracker.RecordLatency(durationMs);
    }

    /// <inheritdoc />
    public void RecordMemoryPressure(double memoryPressurePercent, long allocatedMB, long availableMB)
    {
        PerformanceMetrics.AllocatedMemoryMB.Record(allocatedMB);

        var isHighPressure = _memoryTracker.RecordPressure(memoryPressurePercent);

        if (isHighPressure)
        {
            PerformanceMetrics.HighMemoryPressureAlerts.Add(1);

            if (_logger != null)
            {
                PerformanceMonitorLog.HighMemoryPressureDetected(
                    _logger, memoryPressurePercent, allocatedMB, availableMB);
            }
        }

        var tags = new KeyValuePair<string, object?>[]
        {
            new("pressure_level", GetPressureLevel(memoryPressurePercent))
        };

        PerformanceMetrics.CreateHistogram("honua_memory_pressure_detailed").Record(memoryPressurePercent, tags);
    }

    /// <inheritdoc />
    public void RecordCacheLatency(string cacheType, string operation, TimeSpan duration, bool success = true)
    {
        var tags = new KeyValuePair<string, object?>[]
        {
            new("cache_type", cacheType),
            new("operation", operation),
            new("success", success.ToString().ToLowerInvariant())
        };

        PerformanceMetrics.CacheOperationLatency.Record(duration.TotalMilliseconds, tags);

        if (!success)
        {
            PerformanceMetrics.CacheErrors.Add(1, tags);
        }

        // Track cache latency percentiles
        var latencyKey = $"cache_{cacheType}_{operation}";
        var tracker = _operationLatencyTrackers.GetOrAdd(latencyKey, _ => new LatencyTracker());
        tracker.RecordLatency(duration.TotalMilliseconds);
    }

    /// <inheritdoc />
    public void RecordErrorWithContext(string errorType, string operation, IDictionary<string, object>? context, Exception? exception = null)
    {
        var tags = new List<KeyValuePair<string, object?>>
        {
            new("error_type", errorType),
            new("operation", operation)
        };

        if (exception != null)
        {
            tags.Add(new("exception_type", exception.GetType().Name));
        }

        PerformanceMetrics.ApplicationErrors.Add(1, tags.ToArray());

        if (_logger != null && context != null)
        {
            using var scope = _logger.BeginScope(context);
            PerformanceMonitorLog.ErrorWithContext(_logger, errorType, operation, exception);
        }
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
    /// Gets the pressure level category for the given pressure percentage.
    /// </summary>
    /// <param name="pressurePercent">Memory pressure percentage</param>
    /// <returns>Pressure level category</returns>
    private static string GetPressureLevel(double pressurePercent)
    {
        return pressurePercent switch
        {
            < 50 => "low",
            < 70 => "medium",
            < 85 => "high",
            _ => "critical"
        };
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

        public bool Record(string operation)
        {
            switch (operation.ToLowerInvariant())
            {
                case "hit":
                    Interlocked.Increment(ref _hits);
                    return true;
                case "miss":
                    Interlocked.Increment(ref _misses);
                    return true;
                case "eviction":
                case "pattern_eviction":
                    Interlocked.Increment(ref _evictions);
                    break;
            }

            return false;
        }

        public double GetHitRatio()
        {
            var hits = Interlocked.Read(ref _hits);
            var misses = Interlocked.Read(ref _misses);
            var total = hits + misses;
            return total > 0 ? (double)hits / total : 0.0;
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

    private sealed class RequestMetricsAggregator
    {
        private const int SampleSize = 512;
        private readonly double _slowThresholdMs;
        private readonly double[] _latencySamples = new double[SampleSize];
        private int _latencyIndex = -1;
        private long _totalRequests;
        private long _totalServerErrors;
        private long _totalClientErrors;
        private long _totalDurationMicros;
        private long _maxDurationMicros;
        private long _slowRequestCount;
        private int _activeRequests;

        public RequestMetricsAggregator(double slowThresholdMs)
        {
            _slowThresholdMs = slowThresholdMs > 0 ? slowThresholdMs : 1000.0;
        }

        public void RecordRequest(int statusCode, double durationMs)
        {
            Interlocked.Increment(ref _totalRequests);

            if (statusCode >= 500)
            {
                Interlocked.Increment(ref _totalServerErrors);
            }
            else if (statusCode >= 400)
            {
                Interlocked.Increment(ref _totalClientErrors);
            }

            var durationMicros = (long)Math.Round(durationMs * 1000.0);
            Interlocked.Add(ref _totalDurationMicros, durationMicros);
            UpdateMax(ref _maxDurationMicros, durationMicros);

            if (durationMs >= _slowThresholdMs)
            {
                Interlocked.Increment(ref _slowRequestCount);
            }

            var index = Interlocked.Increment(ref _latencyIndex);
            _latencySamples[index % SampleSize] = durationMs;
        }

        public void RecordActiveDelta(int delta)
        {
            Interlocked.Add(ref _activeRequests, delta);
        }

        public HttpRequestMetricsSnapshot Snapshot()
        {
            var totalRequests = Interlocked.Read(ref _totalRequests);
            var totalDurationMicros = Interlocked.Read(ref _totalDurationMicros);
            var avgDurationMs = totalRequests > 0
                ? (totalDurationMicros / 1000.0) / totalRequests
                : 0.0;

            var maxDurationMs = Interlocked.Read(ref _maxDurationMicros) / 1000.0;
            var p95 = CalculateP95();

            return new HttpRequestMetricsSnapshot
            {
                TotalRequests = totalRequests,
                TotalServerErrors = Interlocked.Read(ref _totalServerErrors),
                TotalClientErrors = Interlocked.Read(ref _totalClientErrors),
                ActiveRequests = Volatile.Read(ref _activeRequests),
                AvgDurationMs = avgDurationMs,
                MaxDurationMs = maxDurationMs,
                P95DurationMs = p95,
                SlowRequestCount = Interlocked.Read(ref _slowRequestCount),
                SlowRequestThresholdMs = _slowThresholdMs
            };
        }

        private double CalculateP95()
        {
            var snapshot = new double[_latencySamples.Length];
            Array.Copy(_latencySamples, snapshot, snapshot.Length);

            var values = snapshot.Where(value => value > 0).ToArray();
            if (values.Length == 0)
            {
                return 0.0;
            }

            Array.Sort(values);
            var index = (int)Math.Ceiling(values.Length * 0.95) - 1;
            if (index < 0)
            {
                index = 0;
            }

            return values[index];
        }

        private static void UpdateMax(ref long target, long value)
        {
            long current;
            while ((current = Volatile.Read(ref target)) < value)
            {
                if (Interlocked.CompareExchange(ref target, value, current) == current)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Tracks latency metrics and calculates percentiles.
    /// </summary>
    private sealed class LatencyTracker
    {
        private const int SampleSize = 100;
        private readonly double[] _samples = new double[SampleSize];
        private int _index = -1;
        private readonly object _lock = new();

        public void RecordLatency(double latencyMs)
        {
            lock (_lock)
            {
                var nextIndex = Interlocked.Increment(ref _index);
                _samples[nextIndex % SampleSize] = latencyMs;
            }
        }

        public LatencyPercentiles GetPercentiles()
        {
            lock (_lock)
            {
                var values = _samples.Where(v => v > 0).ToArray();
                if (values.Length == 0)
                {
                    return new LatencyPercentiles(0, 0, 0, 0);
                }

                Array.Sort(values);

                var p50Index = (int)(values.Length * 0.5) - 1;
                var p95Index = (int)(values.Length * 0.95) - 1;
                var p99Index = (int)(values.Length * 0.99) - 1;

                p50Index = Math.Max(0, p50Index);
                p95Index = Math.Max(0, p95Index);
                p99Index = Math.Max(0, p99Index);

                return new LatencyPercentiles(
                    values.Average(),
                    values[p50Index],
                    values[p95Index],
                    values[p99Index]);
            }
        }
    }

    /// <summary>
    /// Represents latency percentile measurements.
    /// </summary>
    private sealed record LatencyPercentiles(double Average, double P50, double P95, double P99);

    /// <summary>
    /// Tracks memory pressure and triggers alerts for sustained high pressure.
    /// </summary>
    private sealed class MemoryPressureTracker
    {
        private const int HighPressureThreshold = 80;
        private const int CriticalPressureThreshold = 90;
        private const int ConsecutiveHighPressureLimit = 3;

        private readonly Queue<double> _recentMeasurements = new(10);
        private int _consecutiveHighPressure;
        private DateTime _lastAlertTime = DateTime.MinValue;
        private readonly TimeSpan _alertCooldown = TimeSpan.FromMinutes(5);

        public bool RecordPressure(double pressurePercent)
        {
            _recentMeasurements.Enqueue(pressurePercent);
            if (_recentMeasurements.Count > 10)
            {
                _recentMeasurements.Dequeue();
            }

            var isHighPressure = pressurePercent >= HighPressureThreshold;

            if (isHighPressure)
            {
                _consecutiveHighPressure++;
            }
            else
            {
                _consecutiveHighPressure = 0;
            }

            // Trigger alert if we have sustained high pressure and haven't alerted recently
            var shouldAlert = (_consecutiveHighPressure >= ConsecutiveHighPressureLimit ||
                              pressurePercent >= CriticalPressureThreshold) &&
                             DateTime.UtcNow - _lastAlertTime > _alertCooldown;

            if (shouldAlert)
            {
                _lastAlertTime = DateTime.UtcNow;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Logging definitions for performance monitoring.
    /// </summary>
    private static partial class PerformanceMonitorLog
    {
        [LoggerMessage(7001, LogLevel.Warning,
            "High memory pressure detected: {PressurePercent:F1}% (Allocated: {AllocatedMB}MB, Available: {AvailableMB}MB)")]
        public static partial void HighMemoryPressureDetected(
            ILogger logger, double pressurePercent, long allocatedMB, long availableMB);

        [LoggerMessage(7002, LogLevel.Error,
            "Error in operation {Operation} of type {ErrorType}")]
        public static partial void ErrorWithContext(
            ILogger logger, string errorType, string operation, Exception? exception);
    }
}
