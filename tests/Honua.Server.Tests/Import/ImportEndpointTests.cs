// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for file import endpoints
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class ImportEndpointTests : IAsyncLifetime
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
    [Endpoint("GET /api/v1/admin/import/formats")]
    public async Task GetSupportedFormats_V1_ReturnsAllSupportedExtensions()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(".geojson");
        content.Should().Contain(".kml");
        content.Should().Contain(".gpkg");
        content.Should().Contain(".gpx");
        content.Should().Contain(".zip");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/formats")]
    public async Task GetSupportedFormats_RepeatRequest_ReturnsAllSupportedExtensions()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(".geojson");
        content.Should().Contain(".kml");
        content.Should().Contain(".gpkg");
        content.Should().Contain(".gpx");
        content.Should().Contain(".zip");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_V1_WithValidGeoJson_ReturnsPreview()
    {
        // Arrange
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "San Francisco",
                        "population": 883305
                    }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("GeoJson");
        responseContent.Should().Contain("San Francisco");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_RepeatRequest_WithValidGeoJson_ReturnsPreview()
    {
        // Arrange
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "San Francisco",
                        "population": 883305
                    }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("GeoJson");
        responseContent.Should().Contain("San Francisco");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_WithEmptyFile_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent("", Encoding.UTF8);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "empty.geojson"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task PreviewFile_WithUnsupportedFormat_ReturnsBadRequest()
    {
        // Arrange
        var content = new MultipartFormDataContent();
        var fileContent = new StringContent("some content", Encoding.UTF8);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "test.txt"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Unsupported file format");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_V1_WithValidRequest_ReturnsImportResult()
    {
        // Arrange
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "Test Point",
                        "type": "landmark"
                    }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();

        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        content.Add(new StringContent("test_import_table_v1"), "TableName");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("test_import_table_v1");
        responseContent.Should().Contain("GeoJson");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_RepeatRequest_WithValidRequest_ReturnsImportResult()
    {
        // Arrange
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Point",
                        "coordinates": [-122.4194, 37.7749]
                    },
                    "properties": {
                        "name": "Test Point",
                        "type": "landmark"
                    }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();

        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        content.Add(new StringContent("test_import_table"), "TableName");
        content.Add(new StringContent("4326"), "TargetSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("test_import_table");
        responseContent.Should().Contain("GeoJson");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_WithInvalidTableName_ReturnsBadRequest()
    {
        // Arrange
        var geoJsonContent = """{"type": "FeatureCollection", "features": []}""";

        var content = new MultipartFormDataContent();

        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        content.Add(new StringContent("invalid-table-name!"), "TableName"); // Invalid characters

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Invalid table name");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_WithMissingTableName_ReturnsBadRequest()
    {
        // Arrange
        var geoJsonContent = """{"type": "FeatureCollection", "features": []}""";

        var content = new MultipartFormDataContent();

        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8);
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.geojson"
        };
        content.Add(fileContent);

        // Act (no TableName provided)
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Table name is required");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/limits")]
    public async Task GetImportLimits_V1_ReturnsLimitsConfiguration()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/limits");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        // Should contain limit configuration properties
        content.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/limits")]
    public async Task GetImportLimits_RepeatRequest_ReturnsLimitsConfiguration()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/limits");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs")]
    public async Task GetActiveJobs_V1_ReturnsJobsList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs");

        // Assert - Returns 200 even if no jobs or service unavailable returns 503
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs")]
    public async Task GetActiveJobs_RepeatRequest_ReturnsJobsList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task GetJobStatus_V1_WithInvalidJobId_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs/nonexistent-job");

        // Assert - Returns 404 for non-existent job or 503 if service unavailable
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task GetJobStatus_RepeatRequest_WithInvalidJobId_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs/nonexistent-job");

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/jobs/{jobId}/cancel")]
    public async Task CancelJob_V1_WithInvalidJobId_Returns404()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/jobs/nonexistent-job/cancel", null);

        // Assert - Returns 404 for non-existent job or 503 if service unavailable
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/jobs/{jobId}/cancel")]
    public async Task CancelJob_RepeatRequest_WithInvalidJobId_Returns404()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/jobs/nonexistent-job/cancel", null);

        // Assert
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/uploads")]
    public async Task GetActiveUploads_ReturnsUploadsList()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/uploads");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/uploads/{uploadId}/progress")]
    public async Task GetUploadProgress_WithUnknownUploadId_ReturnsNotFound()
    {
        var response = await _client.GetAsync("/api/v1/admin/import/uploads/nonexistent-upload/progress");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/uploads/{uploadId}/cancel")]
    public async Task CancelUpload_WithUnknownUploadId_ReturnsNotFound()
    {
        var response = await _client.PostAsync("/api/v1/admin/import/uploads/nonexistent-upload/cancel", null);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.ServiceUnavailable);
    }
}
