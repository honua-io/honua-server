// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

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
    public async Task Upload_WithLargeSingleRowGroup_RejectsWithError()
    {
        // Arrange — file exceeds GeoParquetReader.MaxRowsPerRowGroup (100,000)
        // in a single row group. Service must reject before streaming features.
        await using var stream = await GeoParquetTestFactory.CreateStreamAsync(
            rowCount: 100_001,
            crs: GeoParquetTestFactory.CrsStyle.PropertiesName);
        var fileBytes = stream.ToArray();

        var content = CreateUploadContent(fileBytes, "large_rg.parquet", "geoparquet_large_rg_test");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("geoparquet_large_rg_test");
        responseContent.Should().Contain("\"success\":false");
        responseContent.Should().Contain("row group");
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
