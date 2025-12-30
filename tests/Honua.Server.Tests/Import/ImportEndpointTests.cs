// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for file import endpoints
/// </summary>
[Collection("Database")]
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
    [Endpoint("GET /api/import/formats")]
    public async Task GetSupportedFormats_ReturnsAllSupportedExtensions()
    {
        // Act
        var response = await _client.GetAsync("/api/import/formats");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain(".geojson");
        content.Should().Contain(".kml");
        content.Should().Contain(".gpkg");
        content.Should().Contain(".gpx");
        content.Should().Contain(".shp");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/preview")]
    public async Task PreviewFile_WithValidGeoJson_ReturnsPreview()
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
        var response = await _client.PostAsync("/api/import/preview", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("GeoJson");
        responseContent.Should().Contain("San Francisco");
    }

    [IntegrationTest]
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
        var response = await _client.PostAsync("/api/import/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
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
        var response = await _client.PostAsync("/api/import/preview", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Unsupported file format");
    }

    [IntegrationTest]
    [Endpoint("POST /api/import/upload")]
    public async Task ImportFile_WithValidRequest_ReturnsImportResult()
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
        var response = await _client.PostAsync("/api/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("test_import_table");
        responseContent.Should().Contain("GeoJson");
    }

    [IntegrationTest]
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
        var response = await _client.PostAsync("/api/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Invalid table name");
    }

    [IntegrationTest]
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
        var response = await _client.PostAsync("/api/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("Table name is required");
    }
}
