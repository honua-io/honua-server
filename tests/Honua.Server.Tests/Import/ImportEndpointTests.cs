// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using NSubstitute;

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

    private static bool TryGetAllowValues(HttpResponseMessage response, out IEnumerable<string> values)
    {
        if (response.Headers.TryGetValues("Allow", out var headerValues))
        {
            values = headerValues;
            return true;
        }

        if (response.Content.Headers.TryGetValues("Allow", out var contentHeaderValues))
        {
            values = contentHeaderValues;
            return true;
        }

        values = Array.Empty<string>();
        return false;
    }

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
        content.Should().Contain(".csv");
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
        content.Should().Contain(".csv");
        content.Should().Contain(".zip");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/formats")]
    [Endpoint("PUT /api/v1/admin/import/formats")]
    [Endpoint("DELETE /api/v1/admin/import/formats")]
    [Endpoint("PATCH /api/v1/admin/import/formats")]
    public async Task GetSupportedFormats_WithWrongHttpMethods_Returns405()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/formats"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
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
    [Endpoint("GET /api/v1/admin/import/preview")]
    [Endpoint("PUT /api/v1/admin/import/preview")]
    [Endpoint("DELETE /api/v1/admin/import/preview")]
    [Endpoint("PATCH /api/v1/admin/import/preview")]
    public async Task PreviewFile_WithWrongHttpMethods_Returns405()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/preview"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/upload-url")]
    [Endpoint("PUT /api/v1/admin/import/upload-url")]
    [Endpoint("DELETE /api/v1/admin/import/upload-url")]
    [Endpoint("PATCH /api/v1/admin/import/upload-url")]
    public async Task ImportFileFromUrl_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/upload-url"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("POST");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/limits")]
    [Endpoint("PUT /api/v1/admin/import/limits")]
    [Endpoint("DELETE /api/v1/admin/import/limits")]
    [Endpoint("PATCH /api/v1/admin/import/limits")]
    public async Task GetImportLimits_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/limits"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/preview-url")]
    [Endpoint("PUT /api/v1/admin/import/preview-url")]
    [Endpoint("DELETE /api/v1/admin/import/preview-url")]
    [Endpoint("PATCH /api/v1/admin/import/preview-url")]
    public async Task PreviewFileFromUrl_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/preview-url"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("POST");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/upload")]
    [Endpoint("PUT /api/v1/admin/import/upload")]
    [Endpoint("DELETE /api/v1/admin/import/upload")]
    [Endpoint("PATCH /api/v1/admin/import/upload")]
    public async Task ImportFile_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/upload"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("POST");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/uploads/{uploadId}/progress")]
    [Endpoint("PUT /api/v1/admin/import/uploads/{uploadId}/progress")]
    [Endpoint("DELETE /api/v1/admin/import/uploads/{uploadId}/progress")]
    [Endpoint("PATCH /api/v1/admin/import/uploads/{uploadId}/progress")]
    public async Task GetUploadProgress_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/uploads/test-upload/progress"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/uploads/{uploadId}/cancel")]
    [Endpoint("PUT /api/v1/admin/import/uploads/{uploadId}/cancel")]
    [Endpoint("DELETE /api/v1/admin/import/uploads/{uploadId}/cancel")]
    [Endpoint("PATCH /api/v1/admin/import/uploads/{uploadId}/cancel")]
    public async Task CancelUpload_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/uploads/test-upload/cancel"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("POST");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/uploads")]
    [Endpoint("PUT /api/v1/admin/import/uploads")]
    [Endpoint("DELETE /api/v1/admin/import/uploads")]
    [Endpoint("PATCH /api/v1/admin/import/uploads")]
    public async Task GetActiveUploads_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/uploads"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/jobs")]
    [Endpoint("PUT /api/v1/admin/import/jobs")]
    [Endpoint("DELETE /api/v1/admin/import/jobs")]
    [Endpoint("PATCH /api/v1/admin/import/jobs")]
    public async Task GetActiveJobs_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/jobs"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("PUT /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("DELETE /api/v1/admin/import/jobs/{jobId}")]
    [Endpoint("PATCH /api/v1/admin/import/jobs/{jobId}")]
    public async Task GetImportJobStatus_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "POST", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/jobs/test-job"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("GET");
        }
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}/cancel")]
    [Endpoint("PUT /api/v1/admin/import/jobs/{jobId}/cancel")]
    [Endpoint("DELETE /api/v1/admin/import/jobs/{jobId}/cancel")]
    [Endpoint("PATCH /api/v1/admin/import/jobs/{jobId}/cancel")]
    public async Task CancelImportJob_WithWrongHttpMethods_Returns405AndAllowHeader()
    {
        foreach (var method in new[] { "GET", "PUT", "DELETE", "PATCH" })
        {
            var response = await _client.SendAsync(
                new HttpRequestMessage(new HttpMethod(method), "/api/v1/admin/import/jobs/test-job/cancel"));

            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            TryGetAllowValues(response, out var allowedValues).Should().BeTrue();
            allowedValues.Should().ContainSingle().Which.Should().Be("POST");
        }
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
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task ImportFile_WithCloudUploadFailure_DoesNotLeakInternalErrorDetails()
    {
        const string sensitiveError = "Cloud timeout: bucket=prod-internal secret=abc123";

        var cloudStorage = Substitute.For<ICloudFileStorage>();
        cloudStorage.Provider.Returns(CloudStorageProvider.AwsS3);
        cloudStorage.UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(UploadResult.CreateFailure(sensitiveError)));

        var isolatedFixture = new WebAppFixture().ReplaceService(cloudStorage);
        await isolatedFixture.InitializeAsync();
        try
        {
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
                            "name": "Cloud Upload Failure Test"
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
                FileName = "cloud-failure.geojson"
            };
            content.Add(fileContent);
            content.Add(new StringContent("cloud_failure_import_table"), "TableName");
            content.Add(new StringContent("true"), "ForceBackground");

            var response = await isolatedFixture.Client.PostAsync("/api/v1/admin/import/upload", content);

            response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
            var responseBody = await response.Content.ReadAsStringAsync();
            responseBody.Should().Contain("Failed to upload file to cloud storage.");
            responseBody.Should().NotContain(sensitiveError);
            responseBody.Should().NotContain("secret=abc123");
        }
        finally
        {
            await isolatedFixture.DisposeAsync();
        }
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
