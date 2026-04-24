// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.GeometryService.Models;
using Honua.Server.Features.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.GeometryService;

[Protocol(Protocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceBufferTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_PointGeometry_ReturnsPolygon()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}]
            },
            "inSR": {"latestWkid": 4326},
            "distances": "1000",
            "unit": "esriMeters",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPolygon");
        result.Geometries.Should().HaveCount(1);
        var geom = result.Geometries![0];
        geom.GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GeodesicProjectedInSR_ReturnsPolygon()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": "3857",
            "distances": "1000",
            "unit": "esriMeters",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        var geom = result.Geometries![0];
        geom.GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_NonGeodesicGeographicInput_UsesLinearDistanceUnits()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": "4326",
            "distances": "1000",
            "unit": "esriMeters",
            "geodesic": "false"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var ring = result.Geometries![0].GetProperty("rings")[0];
        var minX = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        foreach (var point in ring.EnumerateArray())
        {
            var x = point[0].GetDouble();
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
        }

        var widthDegrees = maxX - minX;
        widthDegrees.Should().BeGreaterThan(0.001);
        widthDegrees.Should().BeLessThan(0.1,
            "a 1km buffer in EPSG:4326 should be a small angular distance, not hundreds of degrees");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_MultipleDistances_ReturnsMultipleGeometries()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}},
                    {"x": -73.9857, "y": 40.7484, "spatialReference": {"wkid": 4326}}
                ]
            },
            "inSR": "4326",
            "distances": "500,1000",
            "unit": "esriMeters",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(4, "ArcGIS buffers each input geometry at each requested distance");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_UnionResults_ReturnsSingleGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}},
                    {"x": -122.4180, "y": 37.7760, "spatialReference": {"wkid": 4326}}
                ]
            },
            "inSR": "4326",
            "distances": "5000",
            "unit": "esriMeters",
            "unionResults": "true",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1, "union should combine into a single geometry");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_MultipleDistancesWithUnionResults_ReturnsOneGeometryPerDistance()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}},
                    {"x": -122.4180, "y": 37.7760, "spatialReference": {"wkid": 4326}}
                ]
            },
            "inSR": "4326",
            "distances": "500,1000",
            "unit": "esriMeters",
            "unionResults": "true",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(2, "ArcGIS unions all buffers at each requested distance");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_InvalidGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": []
            },
            "inSR": "4326",
            "distances": "100"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_MalformedGeometryPayload_DoesNotLeakParserDetails()
    {
        const string sentinel = "GEOMETRY_SENTINEL";
        var body = $$"""
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": "{{sentinel}}", "y": 37.7749}]
            },
            "inSR": "4326",
            "distances": "100"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be400BadRequest();

        var responseContent = await response.Content.ReadAsStringAsync();
        var error = JsonSerializer.Deserialize<ApiErrorResponse>(responseContent);
        error.Should().NotBeNull();
        error!.Error.Message.Should().Be("Bad Request");
        error.Error.Details.Should().NotBeNull();
        error.Error.Details!.Should().Contain(detail => detail.Contains("Invalid geometry input.", StringComparison.Ordinal));
        responseContent.Should().NotContain(sentinel);
        responseContent.Should().NotContain("BytePositionInLine");
        responseContent.Should().NotContain("LineNumber");
        responseContent.Should().NotContain("System.Text.Json");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_MissingDistance_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}]
            },
            "inSR": "4326"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_WithTooManyGeometries_Returns400()
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            geometries = new
            {
                geometryType = "esriGeometryPoint",
                geometries = Enumerable.Range(0, 1001)
                    .Select(i => new { x = i * 0.001, y = i * 0.001 })
                    .ToArray()
            },
            inSR = "4326",
            distances = "1",
            unit = "esriMeters"
        });

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/buffer",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("maximum of 1000 geometries");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_WithTooManyDistances_Returns400()
    {
        var distances = string.Join(",", Enumerable.Range(1, 1001));
        var requestBody = JsonSerializer.Serialize(new
        {
            geometries = new
            {
                geometryType = "esriGeometryPoint",
                geometries = new[]
                {
                    new { x = -122.4194, y = 37.7749 }
                }
            },
            inSR = "4326",
            distances,
            unit = "esriMeters"
        });

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/buffer",
            new StringContent(requestBody, Encoding.UTF8, "application/json"));

        response.Be400BadRequest();

        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("maximum of 1000 values");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GetWithQueryString_ReturnsPolygon()
    {
        var url = "/rest/services/Utilities/Geometry/GeometryServer/buffer" +
            "?geometries=%7B%22geometryType%22%3A%22esriGeometryPoint%22%2C%22geometries%22%3A%5B%7B%22x%22%3A-122.4194%2C%22y%22%3A37.7749%7D%5D%7D" +
            "&inSR=4326&distances=1000&unit=esriMeters&geodesic=true";

        var response = await _fixture.Client.GetAsync(url);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPolygon");
        result.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer?inSR=4326");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_JsonSpatialReference_ParsesCorrectly()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749}]
            },
            "inSR": {"wkid": 4326},
            "distances": "1000",
            "unit": "9001",
            "geodesic": "true"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }
}
