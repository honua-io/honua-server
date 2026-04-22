// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Monitors database query performance and detects slow queries for optimization opportunities.
/// Provides detailed telemetry for query execution patterns and performance bottlenecks.
/// </summary>
public interface IDatabaseQueryPerformanceMonitor
{
    /// <summary>
    /// Starts monitoring a database query operation.
    /// </summary>
    /// <param name="queryType">Type/category of the query</param>
    /// <param name="correlationId">Correlation ID for distributed tracing</param>
    /// <returns>Query execution context for tracking</returns>
    IQueryExecutionContext StartQueryExecution(string queryType, string correlationId);

    /// <summary>
    /// Gets performance statistics for query operations.
    /// </summary>
    QueryPerformanceStatistics GetStatistics();

    /// <summary>
    /// Gets recent slow queries for analysis.
    /// </summary>
    IReadOnlyList<SlowQueryRecord> GetRecentSlowQueries(int maxCount = 100);
}

/// <summary>
/// Context for tracking a specific query execution.
/// </summary>
public interface IQueryExecutionContext : IDisposable
{
    /// <summary>
    /// Records the completion of the query execution.
    /// </summary>
    /// <param name="success">Whether the query executed successfully</param>
    /// <param name="rowCount">Number of rows affected/returned</param>
    /// <param name="additionalMetrics">Additional performance metrics</param>
    void Complete(bool success, long? rowCount = null, IDictionary<string, object>? additionalMetrics = null);

    /// <summary>
    /// Records an exception that occurred during query execution.
    /// </summary>
    /// <param name="exception">The exception that occurred</param>
    void RecordException(Exception exception);
}

/// <summary>
/// Configuration options for database query performance monitoring.
/// </summary>
public sealed class QueryPerformanceMonitoringOptions
{
    /// <summary>
    /// Threshold in milliseconds above which queries are considered slow.
    /// </summary>
    public double SlowQueryThresholdMs { get; set; } = 1000; // 1 second

    /// <summary>
    /// Maximum number of slow queries to retain in memory.
    /// </summary>
    public int MaxSlowQueryHistory { get; set; } = 1000;

    /// <summary>
    /// Whether to capture query text for slow queries (may contain sensitive data).
    /// </summary>
    public bool CaptureQueryText { get; set; }

    /// <summary>
    /// Maximum length of query text to capture.
    /// </summary>
    public int MaxQueryTextLength { get; set; } = 2048;

    /// <summary>
    /// Threshold for very slow queries that require immediate attention (in milliseconds).
    /// </summary>
    public double CriticalSlowQueryThresholdMs { get; set; } = 5000; // 5 seconds
}

/// <summary>
/// Statistics about query performance across all operations.
/// </summary>
public sealed record QueryPerformanceStatistics
{
    /// <summary>
    /// Total number of queries executed.
    /// </summary>
    public long TotalQueries { get; init; }

    /// <summary>
    /// Number of slow queries detected.
    /// </summary>
    public long SlowQueries { get; init; }

    /// <summary>
    /// Number of failed queries.
    /// </summary>
    public long FailedQueries { get; init; }

    /// <summary>
    /// Average query execution time in milliseconds.
    /// </summary>
    public double AvgExecutionTimeMs { get; init; }

    /// <summary>
    /// 95th percentile query execution time in milliseconds.
    /// </summary>
    public double P95ExecutionTimeMs { get; init; }

    /// <summary>
    /// Maximum query execution time in milliseconds.
    /// </summary>
    public double MaxExecutionTimeMs { get; init; }

    /// <summary>
    /// Performance statistics by query type.
    /// </summary>
    public IReadOnlyDictionary<string, QueryTypeStatistics> ByQueryType { get; init; } =
        new Dictionary<string, QueryTypeStatistics>();

    /// <summary>
    /// Timestamp when statistics were collected.
    /// </summary>
    public DateTime CollectedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Performance statistics for a specific type of query.
/// </summary>
public sealed record QueryTypeStatistics
{
    /// <summary>
    /// Number of queries of this type.
    /// </summary>
    public long Count { get; init; }

    /// <summary>
    /// Average execution time for this query type in milliseconds.
    /// </summary>
    public double AvgTimeMs { get; init; }

    /// <summary>
    /// Maximum execution time for this query type in milliseconds.
    /// </summary>
    public double MaxTimeMs { get; init; }

    /// <summary>
    /// Number of slow queries of this type.
    /// </summary>
    public long SlowCount { get; init; }

    /// <summary>
    /// Number of failed queries of this type.
    /// </summary>
    public long FailedCount { get; init; }
}

/// <summary>
/// Record of a slow query for performance analysis.
/// </summary>
public sealed record SlowQueryRecord
{
    /// <summary>
    /// Correlation ID for distributed tracing.
    /// </summary>
    public string CorrelationId { get; init; } = string.Empty;

    /// <summary>
    /// Type/category of the query.
    /// </summary>
    public string QueryType { get; init; } = string.Empty;

    /// <summary>
    /// Query execution time in milliseconds.
    /// </summary>
    public double ExecutionTimeMs { get; init; }

    /// <summary>
    /// Number of rows affected/returned.
    /// </summary>
    public long? RowCount { get; init; }

    /// <summary>
    /// Timestamp when the query was executed.
    /// </summary>
    public DateTime ExecutedAt { get; init; }

    /// <summary>
    /// Query text (if captured and configured).
    /// </summary>
    public string? QueryText { get; init; }

    /// <summary>
    /// Additional performance metrics.
    /// </summary>
    public IReadOnlyDictionary<string, object>? AdditionalMetrics { get; init; }

    /// <summary>
    /// Whether this query failed.
    /// </summary>
    public bool Failed { get; init; }

    /// <summary>
    /// Exception information if the query failed.
    /// </summary>
    public string? ExceptionType { get; init; }

    /// <summary>
    /// Exception message if the query failed.
    /// </summary>
    public string? ExceptionMessage { get; init; }
}

/// <summary>
/// Production implementation of database query performance monitoring.
/// </summary>
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

        var result = new List<SlowQueryRecord>();
        var count = 0;

        // Get the most recent slow queries up to maxCount
        while (count < maxCount && _slowQueryRecords.TryDequeue(out var slowQuery))
        {
            result.Add(slowQuery);
            count++;
        }

        // Re-enqueue the items we took (maintaining FIFO order)
        foreach (var query in result.AsEnumerable().Reverse())
        {
            _slowQueryRecords.Enqueue(query);
        }

        return result.OrderByDescending(q => q.ExecutedAt).Take(maxCount).ToList();
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

            // Store execution time in circular buffer for percentile calculation
            var index = Interlocked.Increment(ref _executionTimeIndex) % _executionTimes.Length;
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
            QueryText = _options.CaptureQueryText ? data.QueryText?.Take(_options.MaxQueryTextLength).ToString() : null,
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

    /// <summary>
    /// Internal data structure for tracking query execution metrics by type.
    /// </summary>
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

    /// <summary>
    /// Internal data structure for passing query execution data.
    /// </summary>
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

    /// <summary>
    /// Query execution context implementation.
    /// </summary>
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
