// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.TestKit.Infrastructure;

/// <summary>
/// Factory for creating an <see cref="IFileImportService"/> wired with no-op
/// fakes. Used by preview and format-detection unit tests where no database or
/// external service interaction should occur.
/// </summary>
public static class PreviewImportServiceFactory
{
    public static IFileImportService Create(ImportLimits? limits = null)
    {
        return new StreamingFileImportService(
            new ThrowingConnectionProvider(),
            new NoopCrsDetectionService(),
            new NoopPerformanceMonitor(),
            NullLogger<StreamingFileImportService>.Instance,
            limits);
    }
}

/// <summary>
/// Connection provider that throws on any access. Used by preview and format-detection
/// unit tests where no database interaction should occur.
/// </summary>
public sealed class ThrowingConnectionProvider : IDatabaseConnectionProvider
{
    public Task<DbConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Database access is not expected in preview tests.");

    public Task<(DbConnection Connection, DbTransaction Transaction)> OpenTransactionAsync(
        IsolationLevel isolationLevel = IsolationLevel.RepeatableRead,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Database access is not expected in preview tests.");

    public Task<T> ExecuteWithDeadlockRetryAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Database access is not expected in preview tests.");

    public Task ExecuteWithDeadlockRetryAsync(Func<Task> operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Database access is not expected in preview tests.");
}

/// <summary>
/// CRS detection service that always returns null/false. Used by preview tests
/// where CRS detection is not the concern under test.
/// </summary>
public sealed class NoopCrsDetectionService : ICrsDetectionService
{
    public Task<int?> DetectFromPrjAsync(string prjContent) => Task.FromResult<int?>(null);
    public Task<int?> DetectFromWktAsync(string wktContent) => Task.FromResult<int?>(null);
    public int? DetectFromEpsgCode(string epsgCode) => null;
    public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject) => Task.FromResult<int?>(null);
    public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath) => Task.FromResult<int?>(null);
    public Task<bool> ValidateSridAsync(int srid) => Task.FromResult(false);
}

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
