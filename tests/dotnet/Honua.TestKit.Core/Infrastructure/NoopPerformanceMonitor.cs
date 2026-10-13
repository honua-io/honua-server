// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Monitoring;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Performance monitor that discards all metrics. Used by unit tests
/// where telemetry recording is not the concern under test.
/// </summary>
public sealed class NoopPerformanceMonitor : IPerformanceMonitor
{
    public void RecordDatabaseQuery(string queryType, string layerId, TimeSpan duration, int recordCount)
    {
    }

    public void RecordHttpRequest(string method, string endpoint, int statusCode, TimeSpan duration)
    {
    }

    public void RecordActiveHttpRequestDelta(int delta)
    {
    }

    public void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections)
    {
    }

    public void RecordCacheMetrics(string cacheType, string operation)
    {
    }

    public void RecordTransactionDuration(TimeSpan duration, int operationCount, bool wasCommitted)
    {
    }

    public IOperationScope StartOperation(string operationName) => new NoopOperationScope();

    public void RecordCounter(string name, long value, IDictionary<string, string>? tags = null)
    {
    }

    public void RecordHistogram(string name, double value, IDictionary<string, string>? tags = null)
    {
    }

    public void RecordGeospatialOperation(string operationType, TimeSpan duration, int coordinateCount, int? fromSrid = null, int? toSrid = null)
    {
    }

    public void RecordMemoryPressure(double memoryPressurePercent, long allocatedMB, long availableMB)
    {
    }

    public void RecordCacheLatency(string cacheType, string operation, TimeSpan duration, bool success = true)
    {
    }

    public void RecordErrorWithContext(string errorType, string operation, IDictionary<string, object>? context, Exception? exception = null)
    {
    }
}

/// <summary>
/// Operation scope that does nothing. Used alongside <see cref="NoopPerformanceMonitor"/>.
/// </summary>
public sealed class NoopOperationScope : IOperationScope
{
    public IOperationScope WithTag(string key, string value) => this;

    public void Dispose()
    {
    }
}
