// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IDatabaseQueryPerformanceMonitor
{
    IQueryExecutionContext StartQueryExecution(string queryType, string correlationId);

    QueryPerformanceStatistics GetStatistics();

    IReadOnlyList<SlowQueryRecord> GetRecentSlowQueries(int maxCount = 100);
}

public interface IQueryExecutionContext : IDisposable
{
    void Complete(bool success, long? rowCount = null, IDictionary<string, object>? additionalMetrics = null);

    void RecordException(Exception exception);
}

public sealed class QueryPerformanceMonitoringOptions
{
    public double SlowQueryThresholdMs { get; set; } = 1000; // 1 second

    public int MaxSlowQueryHistory { get; set; } = 1000;

    // Off by default because slow-query text can contain sensitive data.
    public bool CaptureQueryText { get; set; }

    public int MaxQueryTextLength { get; set; } = 2048;

    public double CriticalSlowQueryThresholdMs { get; set; } = 5000; // 5 seconds
}

public sealed record QueryPerformanceStatistics
{
    public long TotalQueries { get; init; }

    public long SlowQueries { get; init; }

    public long FailedQueries { get; init; }

    public double AvgExecutionTimeMs { get; init; }

    public double P95ExecutionTimeMs { get; init; }

    public double MaxExecutionTimeMs { get; init; }

    public IReadOnlyDictionary<string, QueryTypeStatistics> ByQueryType { get; init; } =
        new Dictionary<string, QueryTypeStatistics>();

    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

public sealed record QueryTypeStatistics
{
    public long Count { get; init; }

    public double AvgTimeMs { get; init; }

    public double MaxTimeMs { get; init; }

    public long SlowCount { get; init; }

    public long FailedCount { get; init; }
}

public sealed record SlowQueryRecord
{
    public string CorrelationId { get; init; } = string.Empty;

    public string QueryType { get; init; } = string.Empty;

    public double ExecutionTimeMs { get; init; }

    public long? RowCount { get; init; }

    public DateTime ExecutedAt { get; init; }

    public string? QueryText { get; init; }

    public IReadOnlyDictionary<string, object>? AdditionalMetrics { get; init; }

    public bool Failed { get; init; }

    public string? ExceptionType { get; init; }

    public string? ExceptionMessage { get; init; }
}

internal sealed partial class DatabaseQueryPerformanceMonitor : IDatabaseQueryPerformanceMonitor, IDisposable
{
    private readonly ILogger<DatabaseQueryPerformanceMonitor> _logger;
    private readonly QueryPerformanceMonitoringOptions _options;

    private readonly ConcurrentDictionary<string, QueryTypeMetrics> _queryTypeMetrics = new();
    private readonly ConcurrentQueue<SlowQueryRecord> _slowQueryRecords = new();
    private readonly object _statisticsLock = new();

    private long _totalQueries;
    private long _slowQueriesCount;
    private long _failedQueries;
    private double _totalExecutionTimeMs;
    private double _maxExecutionTimeMs;
    private readonly double[] _executionTimes = new double[10000]; // Circular buffer for percentiles
    private int _executionTimeIndex;
    private volatile bool _disposed;

    public DatabaseQueryPerformanceMonitor(
        ILogger<DatabaseQueryPerformanceMonitor> logger,
        IOptions<QueryPerformanceMonitoringOptions> options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    }

    public IQueryExecutionContext StartQueryExecution(string queryType, string correlationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return new QueryExecutionContext(this, queryType, correlationId, _options);
    }

    public QueryPerformanceStatistics GetStatistics()
    {
        if (_disposed)
            return new QueryPerformanceStatistics();

        lock (_statisticsLock)
        {
            var totalQueries = Interlocked.Read(ref _totalQueries);
            var avgTimeMs = totalQueries > 0 ? _totalExecutionTimeMs / totalQueries : 0;

            // Calculate 95th percentile from circular buffer
            var validTimes = _executionTimes.Take(Math.Min((int)totalQueries, _executionTimes.Length))
                .Where(t => t > 0)
                .OrderBy(t => t)
                .ToArray();

            var p95TimeMs = validTimes.Length > 0
                ? validTimes[(int)(validTimes.Length * 0.95)]
                : 0;

            var byQueryType = _queryTypeMetrics.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.ToStatistics());

            return new QueryPerformanceStatistics
            {
                TotalQueries = totalQueries,
                SlowQueries = Interlocked.Read(ref _slowQueriesCount),
                FailedQueries = Interlocked.Read(ref _failedQueries),
                AvgExecutionTimeMs = avgTimeMs,
                P95ExecutionTimeMs = p95TimeMs,
                MaxExecutionTimeMs = _maxExecutionTimeMs,
                ByQueryType = byQueryType
            };
        }
    }

    public IReadOnlyList<SlowQueryRecord> GetRecentSlowQueries(int maxCount = 100)
    {
        if (_disposed)
            return Array.Empty<SlowQueryRecord>();

        // Snapshot the queue without mutating it, then return the most recent records.
        // ToArray() on ConcurrentQueue is atomic and does not dequeue items, so this read
        // path is non-mutating and safe against concurrent RecordSlowQuery writers.
        return _slowQueryRecords.ToArray()
            .OrderByDescending(q => q.ExecutedAt)
            .Take(maxCount)
            .ToList();
    }

    internal void RecordQueryExecution(QueryExecutionData data)
    {
        if (_disposed) return;

        Interlocked.Increment(ref _totalQueries);

        if (!data.Success)
            Interlocked.Increment(ref _failedQueries);

        // Update global statistics
        lock (_statisticsLock)
        {
            _totalExecutionTimeMs += data.ExecutionTimeMs;
            if (data.ExecutionTimeMs > _maxExecutionTimeMs)
                _maxExecutionTimeMs = data.ExecutionTimeMs;

            // Store execution time in circular buffer for percentile calculation. Mask the sign
            // bit so the index stays non-negative after the int counter wraps past int.MaxValue.
            var index = (Interlocked.Increment(ref _executionTimeIndex) & int.MaxValue) % _executionTimes.Length;
            _executionTimes[index] = data.ExecutionTimeMs;
        }

        // Update query type metrics
        var queryTypeMetrics = _queryTypeMetrics.GetOrAdd(data.QueryType, _ => new QueryTypeMetrics());
        queryTypeMetrics.RecordExecution(
            data.ExecutionTimeMs,
            data.Success,
            data.ExecutionTimeMs >= _options.SlowQueryThresholdMs);

        // Check for slow query
        if (data.ExecutionTimeMs >= _options.SlowQueryThresholdMs)
        {
            Interlocked.Increment(ref _slowQueriesCount);
            RecordSlowQuery(data);
        }

        // Log critical slow queries immediately
        if (data.ExecutionTimeMs >= _options.CriticalSlowQueryThresholdMs)
        {
            MonitoringLog.CriticalSlowQueryDetected(_logger, data.QueryType, data.ExecutionTimeMs, data.CorrelationId);
        }
    }

    private void RecordSlowQuery(QueryExecutionData data)
    {
        var slowQuery = new SlowQueryRecord
        {
            CorrelationId = data.CorrelationId,
            QueryType = data.QueryType,
            ExecutionTimeMs = data.ExecutionTimeMs,
            RowCount = data.RowCount,
            ExecutedAt = data.ExecutedAt,
            QueryText = _options.CaptureQueryText && data.QueryText is { } qt
                ? qt[..Math.Min(qt.Length, _options.MaxQueryTextLength)]
                : null,
            AdditionalMetrics = data.AdditionalMetrics as IReadOnlyDictionary<string, object>,
            Failed = !data.Success,
            ExceptionType = data.Exception?.GetType().Name,
            ExceptionMessage = data.Exception?.Message
        };

        _slowQueryRecords.Enqueue(slowQuery);

        // Maintain max history size
        while (_slowQueryRecords.Count > _options.MaxSlowQueryHistory)
        {
            _slowQueryRecords.TryDequeue(out _);
        }

        MonitoringLog.SlowQueryDetected(_logger, data.QueryType, data.ExecutionTimeMs, data.CorrelationId);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queryTypeMetrics.Clear();
        while (_slowQueryRecords.TryDequeue(out _)) { }
    }

    private sealed class QueryTypeMetrics
    {
        private long _count;
        private long _slowCount;
        private long _failedCount;
        private double _totalTimeMs;
        private double _maxTimeMs;
        private readonly object _lock = new();

        public void RecordExecution(double executionTimeMs, bool success, bool isSlow)
        {
            lock (_lock)
            {
                Interlocked.Increment(ref _count);
                _totalTimeMs += executionTimeMs;

                if (executionTimeMs > _maxTimeMs)
                    _maxTimeMs = executionTimeMs;

                if (isSlow)
                    Interlocked.Increment(ref _slowCount);

                if (!success)
                    Interlocked.Increment(ref _failedCount);
            }
        }

        public QueryTypeStatistics ToStatistics()
        {
            lock (_lock)
            {
                var count = Interlocked.Read(ref _count);
                return new QueryTypeStatistics
                {
                    Count = count,
                    AvgTimeMs = count > 0 ? _totalTimeMs / count : 0,
                    MaxTimeMs = _maxTimeMs,
                    SlowCount = Interlocked.Read(ref _slowCount),
                    FailedCount = Interlocked.Read(ref _failedCount)
                };
            }
        }
    }

    internal sealed record QueryExecutionData
    {
        public string QueryType { get; init; } = string.Empty;
        public string CorrelationId { get; init; } = string.Empty;
        public double ExecutionTimeMs { get; init; }
        public bool Success { get; init; }
        public long? RowCount { get; init; }
        public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
        public string? QueryText { get; init; }
        public IDictionary<string, object>? AdditionalMetrics { get; init; }
        public Exception? Exception { get; init; }
    }

    private sealed class QueryExecutionContext : IQueryExecutionContext
    {
        private readonly DatabaseQueryPerformanceMonitor _monitor;
        private readonly string _queryType;
        private readonly string _correlationId;
        private readonly QueryPerformanceMonitoringOptions _options;
        private readonly Stopwatch _stopwatch;
        private readonly DateTime _startTime;
        private bool _completed;
        private bool _disposed;

        public QueryExecutionContext(
            DatabaseQueryPerformanceMonitor monitor,
            string queryType,
            string correlationId,
            QueryPerformanceMonitoringOptions options)
        {
            _monitor = monitor;
            _queryType = queryType;
            _correlationId = correlationId;
            _options = options;
            _stopwatch = Stopwatch.StartNew();
            _startTime = DateTime.UtcNow;
        }

        public void Complete(bool success, long? rowCount = null, IDictionary<string, object>? additionalMetrics = null)
        {
            if (_completed || _disposed) return;

            _stopwatch.Stop();
            _completed = true;

            var executionData = new QueryExecutionData
            {
                QueryType = _queryType,
                CorrelationId = _correlationId,
                ExecutionTimeMs = _stopwatch.Elapsed.TotalMilliseconds,
                Success = success,
                RowCount = rowCount,
                ExecutedAt = _startTime,
                AdditionalMetrics = additionalMetrics
            };

            _monitor.RecordQueryExecution(executionData);
        }

        public void RecordException(Exception exception)
        {
            if (_completed || _disposed) return;

            _stopwatch.Stop();
            _completed = true;

            var executionData = new QueryExecutionData
            {
                QueryType = _queryType,
                CorrelationId = _correlationId,
                ExecutionTimeMs = _stopwatch.Elapsed.TotalMilliseconds,
                Success = false,
                ExecutedAt = _startTime,
                Exception = exception
            };

            _monitor.RecordQueryExecution(executionData);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (!_completed)
            {
                // Auto-complete on disposal with failure status
                Complete(false);
            }

            _stopwatch?.Stop();
        }
    }

    private static partial class MonitoringLog
    {
        [LoggerMessage(
            EventId = 9001,
            Level = LogLevel.Warning,
            Message = "Slow query detected: {QueryType} took {ExecutionTimeMs:F2}ms [CorrelationId={CorrelationId}]")]
        public static partial void SlowQueryDetected(ILogger logger, string queryType, double executionTimeMs, string correlationId);

        [LoggerMessage(
            EventId = 9002,
            Level = LogLevel.Error,
            Message = "Critical slow query detected: {QueryType} took {ExecutionTimeMs:F2}ms [CorrelationId={CorrelationId}]")]
        public static partial void CriticalSlowQueryDetected(ILogger logger, string queryType, double executionTimeMs, string correlationId);
    }
}
