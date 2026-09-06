// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for GeoParquet import (upload) endpoint.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Admin)]
[Operation(Operations.Import)]
public sealed class GeoParquetImportTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithGeoParquet_ImportsFeaturesToTable()
    {
        // Arrange
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            encoding: "WKB",
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "sample.parquet", "geoparquet_import_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_import_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");

        // honua-server#4396: the response substring above proves the import *reported* success.
        // Read the persisted rows back and assert the values the fixture actually wrote, so an
        // import that committed the wrong geometry, dropped the attributes or reprojected the
        // point cannot pass. The import stages into a provider-chosen physical table
        // (honua_data.imported_geoparquet_import_test), which the response reports through
        // physicalTableName/schema -- use those rather than the caller-supplied logical name.
        var rows = await ReadImportedRowsAsync(responseContent);
        var row = rows.Should().ContainSingle().Subject;
        row.ObjectId.Should().Be(1L);
        row.Name.Should().Be("Test Feature");

        // GeoParquetTestFactory writes POINT(-122.4194 37.7749) in EPSG:4326 and the upload
        // requests TargetSrid=4326, so the stored point must be that point, unmoved.
        row.Geometry.Should().BeOfType<Point>();
        var point = (Point)row.Geometry!;
        point.X.Should().BeApproximately(-122.4194, 1e-9);
        point.Y.Should().BeApproximately(37.7749, 1e-9);
        point.SRID.Should().Be(4326);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNestedColumn_ImportsSuccessfullyWithWarning()
    {
        // Arrange - create file with a StructField column
        await using var stream = await GeoParquetTestFactory.CreateWithNestedColumnAsync(
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "nested.parquet", "geoparquet_nested_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_nested_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("nested type");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNonWkbEncoding_RejectsWithError()
    {
        // Arrange - create file with "point" encoding
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            encoding: "point",
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "point_encoded.parquet", "geoparquet_reject_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_reject_test");
        responseContent.Should().Contain("\"success\":false");
        responseContent.Should().Contain("native geometry encodings");
        responseContent.Should().Contain("WKB");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithNullGeometryRow_SkipsAndCountsAsFailure()
    {
        // Arrange - 2 rows: one with geometry, one with null geometry
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName,
            includeNullGeometryRow: true);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "null_geom.parquet", "geoparquet_null_geom_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_null_geom_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        // Only the row with geometry should be imported
        responseContent.Should().Contain("\"featureCount\":1");
        // Null-geometry skip should be surfaced as a warning
        responseContent.Should().Contain("skipped because geometry was null");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithTimeAndBinaryColumns_ImportsSuccessfully()
    {
        // Arrange - create file with TimeOnly and byte[] attribute columns
        await using var stream = await GeoParquetTestFactory.CreateWithTimeAndBinaryColumnsAsync(
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "time_binary.parquet", "geoparquet_time_binary_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_time_binary_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":1");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithInt16Column_ImportsSuccessfully()
    {
        // Arrange - create file with an INT16 (short) attribute column
        await using var stream = await GeoParquetTestFactory.CreateWithInt16ColumnAsync(
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "int16.parquet", "geoparquet_int16_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_int16_test");
        responseContent.Should().Contain("GeoParquet");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":1");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithLargeSingleRowGroup_ImportsFeatures()
    {
        // pyarrow/GeoPandas writes a 150,000-row table as one row group by default.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 150_000,
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "large_rg.parquet", "geoparquet_large_rg_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_large_rg_test");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":150000");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Upload_WithMissingGeoMetadata_RejectsWithSpecificError()
    {
        // Arrange - a plain Parquet file with no "geo" metadata key.
        // The import path should surface the specific validation message
        // instead of collapsing to the generic "Import failed."
        await using var stream = await GeoParquetTestFactory.CreateWithoutGeoMetadataAsync();
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "plain.parquet", "geoparquet_no_geo_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_no_geo_test");
        responseContent.Should().Contain("\"success\":false");
        // Must surface the specific validation reason, not "Import failed."
        responseContent.Should().Contain("geo");
    }

    /// <summary>
    /// Reads back the rows the import committed, using the physical location the import itself
    /// reported.
    /// </summary>
    /// <remarks>
    /// <para>
    /// honua-server#4396 asks for a readback "through the public API". The file-import surface
    /// creates a raw table and does <em>not</em> register a service layer (there is no
    /// layer-registration call anywhere in <c>src/Honua.Import</c>), so there is no public read
    /// route for an imported table to go through. This reads the committed rows directly, which
    /// is still a readback of persisted state rather than an assertion about the response body.
    /// </para>
    /// <para>
    /// Two provider details drive the query shape and must not be guessed at:
    /// <c>StreamingFileImportService.GetAllowedTableName</c> stages into
    /// <c>imported_&lt;table&gt;</c> inside the operational schema, and
    /// <c>honua.create_import_table</c> materializes only
    /// <c>(id, geometry, properties JSONB, created_at)</c> — source attributes such as
    /// <c>objectid</c> and <c>name</c> live inside <c>properties</c>, not as physical columns.
    /// The physical identifiers are taken from <c>ImportResult.PhysicalTableName</c>/
    /// <c>ImportResult.Schema</c> on the response so the test tracks the provider rather than
    /// re-deriving its naming convention.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<ImportedRow>> ReadImportedRowsAsync(string importResponseJson)
    {
        using var document = JsonDocument.Parse(importResponseJson);
        var root = document.RootElement;

        root.TryGetProperty("physicalTableName", out var physicalTableElement).Should().BeTrue(
            "the import response must report the physical staging table it created");
        root.TryGetProperty("schema", out var schemaElement).Should().BeTrue(
            "the import response must report the schema that owns the physical staging table");

        var physicalTableName = physicalTableElement.GetString();
        var schema = schemaElement.GetString();
        physicalTableName.Should().NotBeNullOrWhiteSpace();
        schema.Should().NotBeNullOrWhiteSpace();

        var reader = new WKBReader();
        var rows = new List<ImportedRow>();

        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();

        // A response that names a table the import never created is itself a failure, and one
        // worth reporting as such rather than as an opaque "relation does not exist".
        await using (var exists = connection.CreateCommand())
        {
            exists.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_catalog.pg_class AS relation
                    INNER JOIN pg_catalog.pg_namespace AS namespace
                        ON namespace.oid = relation.relnamespace
                    WHERE namespace.nspname = @schema
                      AND relation.relname = @table)
                """;
            exists.Parameters.AddWithValue("schema", schema!);
            exists.Parameters.AddWithValue("table", physicalTableName!);
            ((bool?)await exists.ExecuteScalarAsync()).Should().BeTrue(
                "the import reported physical table '{0}'.'{1}', so it must exist",
                schema,
                physicalTableName);
        }

        await using var command = connection.CreateCommand();
        // Both identifiers come from the server's own response, never from client input, and the
        // attribute keys are read as JSONB values rather than interpolated identifiers.
        command.CommandText =
            "SELECT properties ->> 'objectid', properties ->> 'name', ST_AsEWKB(geometry) "
            + $"FROM \"{schema}\".\"{physicalTableName}\" ORDER BY id";

        await using var result = await command.ExecuteReaderAsync();
        while (await result.ReadAsync())
        {
            var geometry = result.IsDBNull(2) ? null : reader.Read((byte[])result.GetValue(2));
            rows.Add(new ImportedRow(
                result.IsDBNull(0) ? null : long.Parse(result.GetString(0), CultureInfo.InvariantCulture),
                result.IsDBNull(1) ? null : result.GetString(1),
                geometry));
        }

        return rows;
    }

    private sealed record ImportedRow(long? ObjectId, string? Name, Geometry? Geometry);

    private static MultipartFormDataContent CreateUploadContent(byte[] fileBytes, string fileName, string tableName)
    {
        var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = fileName
        };
        content.Add(fileContent);
        content.Add(new StringContent(tableName), "TableName");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");
        return content;
    }
}
