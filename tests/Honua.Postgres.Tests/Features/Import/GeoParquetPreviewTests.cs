// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Honua.TestKit;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoParquetPreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoParquet_ReturnsPreviewMetadata()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "sample.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties["name"].Should().Be("Test Feature");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_NoCrsKey_DefaultsToSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.Omitted);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "no_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_ExplicitNullCrs_ReturnsNullSrid()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.ExplicitNull);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "null_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().BeNull();
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_OgcCrs84ProjJson_DefaultsToSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.OgcCrs84ProjJson);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "ogc_crs84_projjson.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_OgcCrs84PropertiesName_DefaultsToSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.OgcCrs84PropertiesName);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "ogc_crs84_propname.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_DecimalEpsgCode_FallsBackToNullSrid()
    {
        // CRS with decimal EPSG code (4326.5) — not a valid integer, should fall through gracefully
        var crs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["authority"] = "EPSG",
                ["code"] = 4326.5
            }
        };
        await using var stream = await GeoParquetTestFactory.CreateWithCustomCrsAsync(crs);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "decimal_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().BeNull();
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_OutOfRangeEpsgCode_FallsBackToNullSrid()
    {
        // CRS with huge EPSG code exceeding Int32 range — should fall through gracefully
        var crs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["authority"] = "EPSG",
                ["code"] = 99999999999L
            }
        };
        await using var stream = await GeoParquetTestFactory.CreateWithCustomCrsAsync(crs);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "huge_crs.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().BeNull();
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_NonWkbEncoding_ThrowsNotSupportedException()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(encoding: "point");
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "non_wkb.parquet");

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*Only WKB encoding is supported*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_SecondaryGeometryColumn_ExcludedFromProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithSecondaryGeometryAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "multi_geom.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("name");
        preview.SampleProperties.Should().NotContainKey("geometry");
        preview.SampleProperties.Should().NotContainKey("geometry2");
        preview.Warnings.Should().Contain(w => w.Contains("multiple geometry columns"));
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_MalformedGeoJson_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMalformedMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "bad_meta.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*malformed*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_NoGeoMetadataKey_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithoutGeoMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "plain.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*missing required*geo*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_WrongShapedMetadata_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithWrongShapedMetadataAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "wrong_shape.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*unexpected structure*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_TotalFeatureCount_ReflectsFullRowCount()
    {
        // Create a file with 5 rows but cap preview samples at 2.
        // TotalFeatureCount comes from Parquet footer metadata (5),
        // not the sample size (2).
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(rowCount: 5);
        var service = CreateService(maxPreviewFeatures: 2);

        var preview = await service.PreviewFileAsync(stream, "multi_row.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(5);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_NullGeometryRow_ExcludedFromSamples()
    {
        // Row 1 has geometry, Row 2 has null geometry
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(includeNullGeometryRow: true);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "null_geom.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        // TotalFeatureCount comes from Parquet footer metadata (all rows)
        preview.TotalFeatureCount.Should().Be(2);
        // Preview samples exclude null-geometry rows
        preview.SampleProperties.Should().ContainKey("name");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_TimeAndBinaryColumns_IncludedInSampleProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithTimeAndBinaryColumnsAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "time_binary.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("event_time");
        // byte[] is converted to base64 in preview path
        preview.SampleProperties.Should().ContainKey("thumbnail");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_Int16Column_IncludedInSampleProperties()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithInt16ColumnAsync();
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "int16.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.TotalFeatureCount.Should().Be(1);
        preview.SampleProperties.Should().ContainKey("priority");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_ProjJsonStringEpsgCode_DetectsSrid4326()
    {
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(crs: GeoParquetTestFactory.CrsStyle.ProjJsonStringCode);
        var service = CreateService();

        var preview = await service.PreviewFileAsync(stream, "string_code.parquet");

        preview.Format.Should().Be(SupportedFileFormat.GeoParquet);
        preview.DetectedSrid.Should().Be(4326);
        preview.TotalFeatureCount.Should().Be(1);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_MissingPrimaryColumn_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMissingPrimaryColumnAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "no_primary_col.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*primary_column*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_MissingColumnsField_ThrowsInvalidDataException()
    {
        await using var stream = await GeoParquetTestFactory.CreateWithMissingColumnsFieldAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "no_columns.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*columns*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_LargeSingleRowGroup_ThrowsInvalidDataException()
    {
        // Create a file with more rows than MaxRowsPerRowGroup (100,000) in a single
        // row group. Parquet.Net materializes the whole group in memory, so the service
        // must reject these files to honour the bounded-memory contract.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 100_001);
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "large_rg.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*row group*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_MismatchedPrimaryColumn_ThrowsInvalidDataException()
    {
        // Geo metadata references "geometry" but the Parquet schema has no such column
        await using var stream = await GeoParquetTestFactory.CreateWithMismatchedPrimaryColumnAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "mismatched.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*geometry*not found*schema*");
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_PrimaryColumnMissingFromGeoColumns_ThrowsInvalidDataException()
    {
        // Parquet schema has "geometry", but geo.columns only describes "other_geom"
        await using var stream = await GeoParquetTestFactory.CreateWithPrimaryColumnMissingFromGeoColumnsAsync();
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "missing_in_columns.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*primary_column*columns*");
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

    [Fact]
    public void DetectFormat_UppercaseExtension_StillDetectsGeoParquet()
    {
        var service = CreateService();

        service.DetectFormat("SAMPLE.PARQUET").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("data.GeoParquet").Should().Be(SupportedFileFormat.GeoParquet);
        service.DetectFormat("mixed.Parquet").Should().Be(SupportedFileFormat.GeoParquet);
    }

    [Fact]
    public void DetectFormat_UppercaseExtension_WorksForPreExistingFormats()
    {
        var service = CreateService();

        service.DetectFormat("data.GEOJSON").Should().Be(SupportedFileFormat.GeoJson);
        service.DetectFormat("ARCHIVE.GPKG").Should().Be(SupportedFileFormat.GeoPackage);
        service.DetectFormat("tracks.GPX").Should().Be(SupportedFileFormat.Gpx);
    }

    [Fact]
    public async Task PreviewFileAsync_GeoParquet_CorruptedFile_ThrowsInvalidDataException()
    {
        // A stream whose contents are not a valid Parquet file. Parquet.Net throws
        // IOException for invalid magic bytes; GeoParquetReader must normalize this
        // to InvalidDataException so the endpoint returns 400, not 500.
        await using var stream = new MemoryStream("not a parquet file"u8.ToArray());
        var service = CreateService();

        var act = () => service.PreviewFileAsync(stream, "corrupted.parquet");

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*not a valid Parquet*");
    }

    private static StreamingFileImportService CreateService(int? maxPreviewFeatures = null)
    {
        var connectionProvider = new ThrowingConnectionProvider();
        var crsDetectionService = new NoopCrsDetectionService();
        var performanceMonitor = new NoopPerformanceMonitor();
        var logger = NullLogger<StreamingFileImportService>.Instance;
        var limits = maxPreviewFeatures.HasValue
            ? new ImportLimits { MaxPreviewFeatures = maxPreviewFeatures.Value }
            : null;

        return new StreamingFileImportService(connectionProvider, crsDetectionService, performanceMonitor, logger, limits);
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
