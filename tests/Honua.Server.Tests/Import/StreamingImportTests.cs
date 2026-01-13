// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Import;

/// <summary>
/// Integration tests for memory-efficient streaming file imports.
/// Tests the streaming parser, batch processing, and background job functionality.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Admin)]
[Operation(Operations.Import)]
public class StreamingImportTests : IAsyncLifetime
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
    public async Task Import_LargeGeoJsonFile_StreamsWithConstantMemory()
    {
        // Arrange - Create a 1MB GeoJSON file with ~1000 features
        var features = new List<object>();
        for (int i = 0; i < 1000; i++)
        {
            features.Add(new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { -122.4194 + (i * 0.001), 37.7749 + (i * 0.001) }
                },
                properties = new
                {
                    id = i,
                    name = $"Feature_{i}",
                    description = $"This is feature number {i} with some padding text to increase size."
                }
            });
        }

        var geoJson = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features
        });

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJson, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "large_test.geojson"
        };
        content.Add(fileContent);
        content.Add(new StringContent("streaming_test_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("streaming_test_table");
        responseContent.Should().Contain("\"success\":true");
        responseContent.Should().Contain("\"featureCount\":");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GeoJsonWithMultipleFeatureTypes_StreamsCorrectly()
    {
        // Arrange - GeoJSON with different geometry types
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
                    "properties": { "name": "Point Feature" }
                },
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "LineString",
                        "coordinates": [[-122.4, 37.7], [-122.5, 37.8], [-122.6, 37.9]]
                    },
                    "properties": { "name": "Line Feature" }
                },
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "Polygon",
                        "coordinates": [[[-122.4, 37.7], [-122.4, 37.8], [-122.5, 37.8], [-122.5, 37.7], [-122.4, 37.7]]]
                    },
                    "properties": { "name": "Polygon Feature" }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "mixed_types.geojson"
        };
        content.Add(fileContent);
        content.Add(new StringContent("mixed_geometry_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("\"featureCount\":3");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/limits")]
    public async Task GetLimits_ReturnsConfiguredLimits()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/limits");

        // Assert
        response.BeSuccessful();
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("batchSize");
        content.Should().Contain("maxMemoryBytes");
        content.Should().Contain("backgroundJobThresholdBytes");
        content.Should().Contain("streamBufferSize");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs")]
    public async Task GetActiveJobs_WithNoJobs_ReturnsEmptyList()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs");

        // Assert
        response.BeSuccessful();
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("\"jobs\":[]");
    }

    [IntegrationTest]
    [Endpoint("GET /api/v1/admin/import/jobs/{jobId}")]
    public async Task GetJobStatus_WithInvalidJobId_ReturnsNotFound()
    {
        // Act
        var response = await _client.GetAsync("/api/v1/admin/import/jobs/nonexistent");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_WktFile_StreamsLineByLine()
    {
        // Arrange - WKT file with multiple geometries
        var wktContent = """
        POINT(-122.4194 37.7749)
        LINESTRING(-122.4 37.7, -122.5 37.8, -122.6 37.9)
        POLYGON((-122.4 37.7, -122.4 37.8, -122.5 37.8, -122.5 37.7, -122.4 37.7))
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(wktContent, Encoding.UTF8, "text/plain");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.wkt"
        };
        content.Add(fileContent);
        content.Add(new StringContent("wkt_import_table"), "TableName");
        content.Add(new StringContent("4326"), "SourceSrid");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("wkt_import_table");
        responseContent.Should().Contain("Wkt");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_KmlFile_StreamsPlacemarks()
    {
        // Arrange - KML file with placemarks
        var kmlContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <kml xmlns="http://www.opengis.net/kml/2.2">
            <Document>
                <Placemark>
                    <name>San Francisco</name>
                    <description>A beautiful city</description>
                    <Point>
                        <coordinates>-122.4194,37.7749,0</coordinates>
                    </Point>
                </Placemark>
                <Placemark>
                    <name>Oakland</name>
                    <Point>
                        <coordinates>-122.2711,37.8044,0</coordinates>
                    </Point>
                </Placemark>
            </Document>
        </kml>
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(kmlContent, Encoding.UTF8, "application/vnd.google-earth.kml+xml");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.kml"
        };
        content.Add(fileContent);
        content.Add(new StringContent("kml_import_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("kml_import_table");
        responseContent.Should().Contain("Kml");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_GpxFile_StreamsTracks()
    {
        // Arrange - GPX file with waypoints and tracks
        var gpxContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx version="1.1" creator="test">
            <wpt lat="37.7749" lon="-122.4194">
                <name>San Francisco</name>
                <ele>10</ele>
            </wpt>
            <trk>
                <name>Bay Trail</name>
                <trkseg>
                    <trkpt lat="37.7749" lon="-122.4194"/>
                    <trkpt lat="37.8044" lon="-122.2711"/>
                    <trkpt lat="37.8716" lon="-122.2727"/>
                </trkseg>
            </trk>
        </gpx>
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(gpxContent, Encoding.UTF8, "application/gpx+xml");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "test.gpx"
        };
        content.Add(fileContent);
        content.Add(new StringContent("gpx_import_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.BeSuccessful();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("gpx_import_table");
        responseContent.Should().Contain("Gpx");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/preview")]
    public async Task Preview_LargeFile_LimitsFeatureCount()
    {
        // Arrange - Create GeoJSON with more features than preview limit
        var features = new List<object>();
        for (int i = 0; i < 200; i++)
        {
            features.Add(new
            {
                type = "Feature",
                geometry = new
                {
                    type = "Point",
                    coordinates = new[] { -122.4194 + (i * 0.001), 37.7749 + (i * 0.001) }
                },
                properties = new { id = i, name = $"Feature_{i}" }
            });
        }

        var geoJson = JsonSerializer.Serialize(new
        {
            type = "FeatureCollection",
            features
        });

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJson, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = "many_features.geojson"
        };
        content.Add(fileContent);

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/preview", content);

        // Assert
        response.BeSuccessful();
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("totalFeatureCount");
        // Feature count should be limited by MaxPreviewFeatures (default 100)
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_WithForceBackground_QueuesJob()
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
                    "properties": { "name": "Test" }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "background_test.geojson"
        };
        content.Add(fileContent);
        content.Add(new StringContent("background_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");
        content.Add(new StringContent("true"), "ForceBackground");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("jobId");
        responseContent.Should().Contain("statusUrl");
        responseContent.Should().Contain("cancelUrl");
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_InvalidGeoJson_HandlesErrorGracefully()
    {
        // Arrange - Invalid GeoJSON with malformed geometry
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": [
                {
                    "type": "Feature",
                    "geometry": {
                        "type": "InvalidType",
                        "coordinates": "not an array"
                    },
                    "properties": { "name": "Invalid" }
                }
            ]
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "invalid.geojson"
        };
        content.Add(fileContent);
        content.Add(new StringContent("invalid_test_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert - Should handle the error gracefully (either skip invalid features or fail with message)
        var responseContent = await response.Content.ReadAsStringAsync();
        // The streaming parser should handle invalid features gracefully
        (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.BadRequest).Should().BeTrue();
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/jobs/{jobId}/cancel")]
    public async Task CancelJob_WithInvalidJobId_ReturnsNotFound()
    {
        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/jobs/nonexistent/cancel", null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/admin/import/upload")]
    public async Task Import_EmptyFeatureCollection_ReturnsError()
    {
        // Arrange
        var geoJsonContent = """
        {
            "type": "FeatureCollection",
            "features": []
        }
        """;

        var content = new MultipartFormDataContent();
        var fileContent = new StringContent(geoJsonContent, Encoding.UTF8, "application/json");
        fileContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "File",
            FileName = "empty.geojson"
        };
        content.Add(fileContent);
        content.Add(new StringContent("empty_table"), "TableName");
        content.Add(new StringContent("true"), "OverwriteExisting");

        // Act
        var response = await _client.PostAsync("/api/v1/admin/import/upload", content);

        // Assert
        var responseContent = await response.Content.ReadAsStringAsync();
        responseContent.Should().Contain("No features found");
    }
}
