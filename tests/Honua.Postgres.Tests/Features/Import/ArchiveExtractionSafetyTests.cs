// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.IO.Compression;
using System.Text;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class ArchiveExtractionSafetyTests
{
    [Fact]
    public async Task PreviewFileAsync_KmzWithExcessiveCompressionRatio_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveCompressionRatio = 10,
            MaxArchiveEntryBytes = 10 * 1024 * 1024,
            MaxArchiveExtractedBytes = 20 * 1024 * 1024
        };

        await using var stream = CreateZipArchive(
            ("doc.kml", Encoding.UTF8.GetBytes(new string('A', 2 * 1024 * 1024))));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.kmz"));

        exception.Message.Should().Contain("compression ratio");
    }

    [Fact]
    public async Task PreviewFileAsync_ShapefileZipExceedingEntryLimit_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveEntryBytes = 1024,
            MaxArchiveExtractedBytes = 10 * 1024,
            MaxArchiveCompressionRatio = 10_000
        };

        await using var stream = CreateZipArchive(
            ("layer.shp", CreatePatternBytes(2048)),
            ("layer.dbf", CreatePatternBytes(128)));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.zip"));

        exception.Message.Should().Contain("maximum uncompressed size");
    }

    [Fact]
    public async Task PreviewFileAsync_ShapefileZipExceedingTotalExtractionLimit_ThrowsInvalidDataException()
    {
        var limits = new ImportLimits
        {
            MaxArchiveEntryBytes = 2000,
            MaxArchiveExtractedBytes = 2500,
            MaxArchiveCompressionRatio = 10_000
        };

        await using var stream = CreateZipArchive(
            ("layer.shp", CreatePatternBytes(1600)),
            ("layer.dbf", CreatePatternBytes(1600)));
        var service = CreateService(limits);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.PreviewFileAsync(stream, "malicious.zip"));

        exception.Message.Should().Contain("maximum total uncompressed size");
    }

    private static StreamingFileImportService CreateService(ImportLimits limits)
    {
        var connectionProvider = new ThrowingConnectionProvider();
        var crsDetectionService = new NoopCrsDetectionService();
        var performanceMonitor = new NoopPerformanceMonitor();
        var logger = NullLogger<StreamingFileImportService>.Instance;

        return new StreamingFileImportService(
            connectionProvider,
            crsDetectionService,
            performanceMonitor,
            logger,
            limits);
    }

    private static MemoryStream CreateZipArchive(params (string Name, byte[] Content)[] entries)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.SmallestSize);
                using var entryStream = entry.Open();
                entryStream.Write(content, 0, content.Length);
            }
        }

        stream.Position = 0;
        return stream;
    }

    private static byte[] CreatePatternBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
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
