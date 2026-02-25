// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Monitoring;
using Honua.Postgres.Features.Import;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Postgres.Tests.Features.Import;

public sealed class GeoPackagePreviewTests
{
    [Fact]
    public async Task PreviewFileAsync_GeoPackage_ReturnsLayerMetadataAndAttributes()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"honua-gpkg-{Guid.NewGuid():N}.gpkg");

        try
        {
            CreateGeoPackage(filePath);

            await using var stream = File.OpenRead(filePath);
            var service = CreateService();

            var preview = await service.PreviewFileAsync(stream, "sample.gpkg");

            preview.Format.Should().Be(SupportedFileFormat.GeoPackage);
            preview.AvailableLayers.Should().Contain("sample_layer");
            preview.DetectedSrid.Should().Be(4326);
            preview.TotalFeatureCount.Should().Be(1);
            preview.SampleProperties.Should().ContainKey("name");
            preview.SampleProperties["name"].Should().Be("Test Feature");
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    private static void CreateGeoPackage(string filePath)
    {
        using var connection = new SqliteConnection($"Data Source={filePath}");
        connection.Open();

        ExecuteNonQuery(connection, """
            CREATE TABLE gpkg_spatial_ref_sys (
                srs_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL PRIMARY KEY,
                organization TEXT NOT NULL,
                organization_coordsys_id INTEGER NOT NULL,
                definition TEXT NOT NULL,
                description TEXT
            );
            """);

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_spatial_ref_sys (srs_name, srs_id, organization, organization_coordsys_id, definition, description)
            VALUES ('WGS 84', 4326, 'EPSG', 4326, 'EPSG:4326', 'WGS 84');
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE gpkg_contents (
                table_name TEXT NOT NULL PRIMARY KEY,
                data_type TEXT NOT NULL,
                identifier TEXT,
                description TEXT DEFAULT '',
                last_change DATETIME NOT NULL,
                min_x DOUBLE,
                min_y DOUBLE,
                max_x DOUBLE,
                max_y DOUBLE,
                srs_id INTEGER
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE gpkg_geometry_columns (
                table_name TEXT NOT NULL,
                column_name TEXT NOT NULL,
                geometry_type_name TEXT NOT NULL,
                srs_id INTEGER NOT NULL,
                z TINYINT NOT NULL,
                m TINYINT NOT NULL,
                PRIMARY KEY (table_name, column_name)
            );
            """);

        ExecuteNonQuery(connection, """
            CREATE TABLE sample_layer (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                geom BLOB,
                name TEXT
            );
            """);

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_contents (table_name, data_type, identifier, description, last_change, srs_id)
            VALUES ('sample_layer', 'features', 'sample_layer', 'Sample layer', CURRENT_TIMESTAMP, 4326);
            """);

        ExecuteNonQuery(connection, """
            INSERT INTO gpkg_geometry_columns (table_name, column_name, geometry_type_name, srs_id, z, m)
            VALUES ('sample_layer', 'geom', 'POINT', 4326, 0, 0);
            """);

        var geometry = new GeometryFactory(new PrecisionModel(), 4326)
            .CreatePoint(new Coordinate(-122.4, 37.6));
        var writer = new GeoPackageGeoWriter();
        var geometryBytes = writer.Write(geometry);

        using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO sample_layer (geom, name) VALUES ($geom, $name);";
        insert.Parameters.AddWithValue("$geom", geometryBytes);
        insert.Parameters.AddWithValue("$name", "Test Feature");
        insert.ExecuteNonQuery();
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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
