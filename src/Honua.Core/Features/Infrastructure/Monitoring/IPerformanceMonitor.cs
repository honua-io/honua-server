// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

public interface IPerformanceMonitor
{
    void RecordDatabaseQuery(string queryType, string layerId, TimeSpan duration, int recordCount);

    void RecordHttpRequest(string method, string endpoint, int statusCode, TimeSpan duration);

    void RecordActiveHttpRequestDelta(int delta);

    void RecordMemoryUsage(long allocatedBytes, int gen0Collections, int gen1Collections, int gen2Collections);

    void RecordCacheMetrics(string cacheType, string operation);

    void RecordTransactionDuration(TimeSpan duration, int operationCount, bool wasCommitted);

    IOperationScope StartOperation(string operationName);

    void RecordCounter(string name, long value, IDictionary<string, string>? tags = null);

    void RecordHistogram(string name, double value, IDictionary<string, string>? tags = null);

    void RecordGeospatialOperation(string operationType, TimeSpan duration, int coordinateCount, int? fromSrid = null, int? toSrid = null);

    void RecordMemoryPressure(double memoryPressurePercent, long allocatedMB, long availableMB);

    void RecordCacheLatency(string cacheType, string operation, TimeSpan duration, bool success = true);

    void RecordErrorWithContext(string errorType, string operation, IDictionary<string, object>? context, Exception? exception = null);
}

public interface IOperationScope : IDisposable
{
    IOperationScope WithTag(string key, string value);
}
