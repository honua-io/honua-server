// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using FlatGeobuf;
using FlatGeobuf.NTS;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class FlatGeobufPreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_FlatGeobuf_ReturnsPreviewMetadata()
    {
        await using var stream = CreateFlatGeobufStream();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.fgb");

        preview.Format.Should().Be(SupportedFileFormat.FlatGeobuf);
        preview.DetectedSrid.Should().Match(srid => !srid.HasValue || srid.Value == 4326);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public void DetectFormat_AndSupportedExtensions_IncludeFlatGeobuf()
    {
        var service = CreateService();

        service.DetectFormat("sample.fgb").Should().Be(SupportedFileFormat.FlatGeobuf);
        service.GetSupportedExtensions().Should().Contain(".fgb");
    }

    private static MemoryStream CreateFlatGeobufStream()
    {
        var attributes = new AttributesTable();
        attributes.Add("name", "Test Feature");

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var feature = new NetTopologySuite.Features.Feature(point, attributes);
        var columns = new List<ColumnMeta>
        {
            new() { Name = "name", Type = ColumnType.String }
        };

        var stream = new MemoryStream();
        FeatureCollectionConversions.Serialize(
            stream,
            new[] { feature },
            FlatGeobuf.GeometryType.Point,
            2,
            columns);
        stream.Position = 0;
        return stream;
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
