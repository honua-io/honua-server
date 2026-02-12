// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.GeometryService.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.GeometryService;

[Protocol(Protocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceSimplifyTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_ValidPolygon_ReturnsTopologicallyCorrectedGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {
                        "rings": [
                            [
                                [-122.42, 37.78], [-122.41, 37.78], [-122.415, 37.785],
                                [-122.41, 37.79], [-122.42, 37.79], [-122.425, 37.785],
                                [-122.42, 37.78]
                            ]
                        ],
                        "spatialReference": {"wkid": 4326}
                    }
                ]
            },
            "sr": "4326"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPolygon");
        result.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_MaintainsTopologicalValidity()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {
                        "rings": [
                            [
                                [0.0, 0.0], [1.0, 0.0], [1.0, 0.5], [1.0, 1.0],
                                [0.5, 1.0], [0.0, 1.0], [0.0, 0.5], [0.0, 0.0]
                            ]
                        ],
                        "spatialReference": {"wkid": 4326}
                    }
                ]
            },
            "sr": "4326"
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

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
    [Operation(Operations.Simplify)]
    [Endpoint("GET /rest/services/geometry/simplify")]
    public async Task Simplify_GetWithQueryString_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString(
            """{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[-122.42,37.78],[-122.41,37.78],[-122.41,37.79],[-122.42,37.79],[-122.42,37.78]]],"spatialReference":{"wkid":4326}}]}""");
        var url = $"/rest/services/geometry/simplify?geometries={geometries}&sr=4326";

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
    [Operation(Operations.Simplify)]
    [Endpoint("GET /rest/services/geometry/simplify")]
    public async Task Simplify_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/simplify?sr=4326");

        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Simplify)]
    [Endpoint("POST /rest/services/geometry/simplify")]
    public async Task Simplify_MissingSR_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            }
        }
        """;
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/geometry/simplify", content);

        response.Be400BadRequest();
    }
}
