// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// Provides standardized performance metrics collection for the Honua Server application.
/// </summary>
/// <remarks>
/// This interface abstracts performance monitoring to support different telemetry providers
/// and enables comprehensive application performance tracking across all layers.
/// </remarks>
public interface IPerformanceMonitor
{
    /// <summary>
    /// Records the duration of a database query operation.
    /// </summary>
    /// <param name="queryType">The type of query (e.g., "select", "insert", "update")</param>
    /// <param name="layerId">The layer identifier the query was executed against</param>
    /// <param name="duration">The query execution duration</param>
    /// <param name="recordCount">Number of records affected or returned</param>
    void RecordDatabaseQuery(string queryType, string layerId, TimeSpan duration, int recordCount);

    /// <summary>
    /// Records the duration of an HTTP request.
    /// </summary>
    /// <param name="method">HTTP method (GET, POST, etc.)</param>
    /// <param name="endpoint">The endpoint path</param>
    /// <param name="statusCode">HTTP response status code</param>
    /// <param name="duration">Request processing duration</param>
    void RecordHttpRequest(string method, string endpoint, int statusCode, TimeSpan duration);

    /// <summary>
    /// Updates the active HTTP request counter by the provided delta.
    /// </summary>
    /// <param name="delta">Positive to increment, negative to decrement</param>
    void RecordActiveHttpRequestDelta(int delta);

    /// <summary>
    /// Records memory usage metrics.
    /// </summary>
    /// <param name="allocatedBytes">Currently allocated memory in bytes</param>
    /// <param name="gen0Collections">Generation 0 garbage collection count</param>
    /// <param name="gen1Collections">Generation 1 garbage collection count</param>
    /// <param name="gen2Collections">Generation 2 garbage collection count</param>
    void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections);

    /// <summary>
    /// Records cache hit/miss metrics.
    /// </summary>
    /// <param name="cacheType">Type of cache (e.g., "layer-metadata", "query-results")</param>
    /// <param name="operation">Cache operation ("hit", "miss", "eviction")</param>
    void RecordCacheMetrics(string cacheType, string operation);

    /// <summary>
    /// Records the duration of a database transaction.
    /// </summary>
    /// <param name="duration">Transaction duration</param>
    /// <param name="operationCount">Number of operations in the transaction</param>
    /// <param name="wasCommitted">Whether the transaction was committed (true) or rolled back (false)</param>
    void RecordTransactionDuration(TimeSpan duration, int operationCount, bool wasCommitted);

    /// <summary>
    /// Creates a scope for measuring operation duration. Dispose the returned object to record the metric.
    /// </summary>
    /// <param name="operationName">Name of the operation being measured</param>
    /// <returns>A disposable scope that records duration when disposed</returns>
    IOperationScope StartOperation(string operationName);

    /// <summary>
    /// Records a custom counter metric.
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="value">Metric value</param>
    /// <param name="tags">Optional tags for metric dimensions</param>
    void RecordCounter(string name, long value, IDictionary<string, string>? tags = null);

    /// <summary>
    /// Records a custom histogram metric for duration measurements.
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="value">Duration value</param>
    /// <param name="tags">Optional tags for metric dimensions</param>
    void RecordHistogram(string name, double value, IDictionary<string, string>? tags = null);
}

/// <summary>
/// Represents a scope for measuring operation duration.
/// </summary>
public interface IOperationScope : IDisposable
{
    /// <summary>
    /// Adds a tag to the operation scope.
    /// </summary>
    /// <param name="key">Tag key</param>
    /// <param name="value">Tag value</param>
    /// <returns>The scope instance for fluent chaining</returns>
    IOperationScope WithTag(string key, string value);
}
