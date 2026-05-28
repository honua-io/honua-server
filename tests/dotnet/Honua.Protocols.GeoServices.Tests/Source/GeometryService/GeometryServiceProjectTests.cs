// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Protocols.GeoServices.GeometryService.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

[Protocol(TestProtocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceProjectTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_Wgs84ToWebMercator_ReturnsCorrectCoordinates()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0, "spatialReference": {"wkid": 4326}}]
            },
            "inSR": "4326",
            "outSR": "3857"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPoint");
        result.Geometries.Should().HaveCount(1);

        var geom = result.Geometries![0];
        geom.TryGetProperty("x", out var x).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(0, 1.0);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_SameSrid_ReturnsUnchanged()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}}]
            },
            "inSR": "4326",
            "outSR": "4326"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var geom = result.Geometries![0];
        geom.TryGetProperty("x", out var x).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(-122.4194, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_InvalidSrid_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": "0",
            "outSR": "4326"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_BatchGeometries_ReturnsAll()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": -122.4194, "y": 37.7749, "spatialReference": {"wkid": 4326}},
                    {"x": -73.9857, "y": 40.7484, "spatialReference": {"wkid": 4326}},
                    {"x": 2.3522, "y": 48.8566, "spatialReference": {"wkid": 4326}}
                ]
            },
            "inSR": "4326",
            "outSR": "3857"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(3, "all input geometries should be projected");
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_GetWithQueryString_ReturnsProjectedGeometry()
    {
        var geometries = Uri.EscapeDataString(
            """{"geometryType":"esriGeometryPoint","geometries":[{"x":0,"y":0}]}""");
        var url = $"/rest/services/Utilities/Geometry/GeometryServer/project?geometries={geometries}&inSR=4326&outSR=3857";

        var response = await _fixture.Client.GetAsync(url);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPoint");
        result.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/project?inSR=4326");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_JsonSpatialReference_WithLatestWkid_ParsesCorrectly()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": {"latestWkid": 4326},
            "outSR": {"latestWkid": 3857}
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_JsonSpatialReference_WithName_ParsesCorrectly()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": {"name": "EPSG:4326"},
            "outSR": {"name": "EPSG:3857"}
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }
}
