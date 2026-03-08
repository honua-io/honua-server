// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Parquet;
using Parquet.Schema;
using ParquetDataColumn = Parquet.Data.DataColumn;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoParquetPreviewTests
{
    private static readonly string[] PointGeometryTypes = ["Point"];
    private static readonly long?[] ObjectIdValues = [1L];
    private static readonly string?[] NameValues = ["Test Feature"];

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_ReturnsPreviewMetadata()
    {
        await using var stream = await CreateGeoParquetStreamAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public void DetectFormat_AndSupportedExtensions_IncludeGeoParquet()
    {
        var service = CreateService();

        service.DetectFormat("sample.parquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("sample.geoparquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.GetSupportedExtensions().Should().Contain(".parquet");
        service.GetSupportedExtensions().Should().Contain(".geoparquet");
    }

    private static async Task<MemoryStream> CreateGeoParquetStreamAsync()
    {
        var objectIdField = new DataField<long?>("objectid");
        var nameField = new DataField<string>("name", true);
        var geometryField = new DataField<byte[]>("geometry", true);
        var schema = new ParquetSchema(objectIdField, nameField, geometryField);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["geo"] = JsonSerializer.Serialize(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["version"] = "1.1.0",
                ["primary_column"] = "geometry",
                ["columns"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["geometry"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["encoding"] = "WKB",
                        ["geometry_types"] = PointGeometryTypes,
                        ["crs"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                            {
                                ["authority"] = "EPSG",
                                ["code"] = 4326
                            }
                        }
                    }
                }
            })
        };

        var point = new Point(-122.4194, 37.7749) { SRID = 4326 };
        var geometryBytes = new WKBWriter().Write(point);

        var stream = new MemoryStream();
        using (var writer = await ParquetWriter.CreateAsync(schema, stream))
        {
            writer.CustomMetadata = metadata;
            using var rowGroupWriter = writer.CreateRowGroup();
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(objectIdField, ObjectIdValues));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(nameField, NameValues));
            await rowGroupWriter.WriteColumnAsync(new ParquetDataColumn(geometryField, new byte[]?[] { geometryBytes }));
        }

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
