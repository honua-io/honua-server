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

public sealed class GeoJsonPreviewNonSeekableTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoJsonNonSeekableStream_ReturnsFirstFeature()
    {
        var geoJson = """
            {
              "type": "FeatureCollection",
              "crs": { "type": "name", "properties": { "name": "EPSG:4326" } },
              "features": [
                {
                  "type": "Feature",
                  "geometry": { "type": "Point", "coordinates": [1, 2] },
                  "properties": { "name": "Test Feature" }
                }
              ]
            }
            """;

        var bytes = Encoding.UTF8.GetBytes(geoJson);
        await using var stream = new NonSeekableStream(new MemoryStream(bytes));
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.geojson");

        preview.Format.Should().Be(SupportedFileFormat.GeoJson);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
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

    private sealed class NonSeekableStream : Stream
    {
        private readonly Stream _inner;

        public NonSeekableStream(Stream inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override int Read(Span<byte> buffer) => _inner.Read(buffer);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync();
            await base.DisposeAsync();
        }
    }
}
