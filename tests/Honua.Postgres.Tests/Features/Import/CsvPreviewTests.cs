// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class CsvPreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_CsvWithLongitudeLatitudeColumns_ReturnsPreviewMetadata()
    {
        var csv = """
            id,name,longitude,latitude,category
            1,San Francisco,-122.4194,37.7749,city
            2,Oakland,-122.2711,37.8044,city
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.csv");

        preview.Format.Should().Be(SupportedFileFormat.Csv);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(2);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("San Francisco");
    }

    [Fact]
    public async Task PreviewFileAsync_CsvWithWktGeometryColumn_ReturnsPreviewMetadata()
    {
        var csv = """
            id,name,wkt
            1,Test Feature,POINT(-122.4194 37.7749)
            """;

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.csv");

        preview.Format.Should().Be(SupportedFileFormat.Csv);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public async Task ReadStreamingAsync_CsvWithExcessivelyLargeRecord_ThrowsInvalidOperationException()
    {
        // Create a CSV with an unbalanced quote that would cause unbounded memory allocation
        // This simulates a malicious CSV designed to exhaust memory
        var csvBuilder = new StringBuilder();
        csvBuilder.AppendLine("id,name,description");
        csvBuilder.Append("1,Test,\"This is a very long field that never closes the quote");

        // Add enough data to exceed the 10MB limit
        var largeData = new string('x', 5 * 1024 * 1024); // 5MB of data
        csvBuilder.AppendLine(largeData);
        csvBuilder.AppendLine(largeData); // Another 5MB, total > 10MB
        csvBuilder.AppendLine("more data to push over the limit");

        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csvBuilder.ToString()));

        // The streaming reader should throw before consuming all available memory
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var feature in CsvFormatReader.ReadStreamingAsync(stream, CancellationToken.None))
            {
                // Should not reach here due to the memory limit
            }
        });

        exception.Message.Should().Contain("CSV record exceeds maximum size limit");
        exception.Message.Should().Contain("10,485,760 bytes");
    }

    private static StreamingFileImportService CreateService()
    {
        var connectionProvider = new ThrowingConnectionProvider();
        var crsDetectionService = new NoopCrsDetectionService();
        var performanceMonitor = new NoopPerformanceMonitor();
        var logger = NullLogger<StreamingFileImportService>.Instance;

        return new StreamingFileImportService(connectionProvider, crsDetectionService, performanceMonitor, logger);
    }

    private sealed class ThrowingConnectionProvider : IDatabaseConnectionProvider
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

    private sealed class NoopCrsDetectionService : ICrsDetectionService
    {
        public Task<int?> DetectFromPrjAsync(string prjContent) => Task.FromResult<int?>(null);
        public Task<int?> DetectFromWktAsync(string wktContent) => Task.FromResult<int?>(null);
        public int? DetectFromEpsgCode(string epsgCode) => null;
        public Task<int?> DetectFromGeoJsonCrsAsync(string crsObject) => Task.FromResult<int?>(null);
        public Task<int?> DetectFromShapefilePrjAsync(string shapefilePath) => Task.FromResult<int?>(null);
        public Task<bool> ValidateSridAsync(int srid) => Task.FromResult(false);
    }

    private sealed class NoopPerformanceMonitor : IPerformanceMonitor
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

    private sealed class NoopOperationScope : IOperationScope
    {
        public IOperationScope WithTag(string key, string value) => this;

        public void Dispose()
        {
        }
    }
}
