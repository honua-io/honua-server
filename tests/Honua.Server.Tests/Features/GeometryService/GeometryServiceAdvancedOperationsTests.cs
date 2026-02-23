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
public sealed class GeometryServiceAdvancedOperationsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/geometry/intersect")]
    public async Task Intersect_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {
                        "rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]
                    }
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("GET /rest/services/geometry/intersect")]
    public async Task Intersect_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/intersect?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/geometry/union")]
    public async Task Union_PostValidRequest_ReturnsSingleGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]},
                    {"rings": [[[1,0],[2,0],[2,1],[1,1],[1,0]]]}
                ]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/union",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("GET /rest/services/geometry/union")]
    public async Task Union_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/union");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/geometry/clip")]
    public async Task Clip_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/clip",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/geometry/clip")]
    public async Task Clip_NonRectangularClipGeometry_UsesEnvelopeForClipping()
    {
        // The clip geometry is a triangle with vertices at (1,1), (3,1), (2,3).
        // Its envelope is the rectangle (1,1)-(3,3).
        // The target polygon is (0,0)-(4,4).
        // If clip correctly uses the envelope, the result should be a rectangle (1,1)-(3,3),
        // NOT the intersection with the triangle itself.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[4,0],[4,4],[0,4],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[2,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var clipResponse = await _fixture.Client.PostAsync(
            "/rest/services/geometry/clip",
            new StringContent(body, Encoding.UTF8, "application/json"));

        clipResponse.Be200Ok();
        var clipContent = await clipResponse.Content.ReadAsStringAsync();
        var clipResult = JsonSerializer.Deserialize(clipContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        clipResult.Should().NotBeNull();
        clipResult!.Geometries.Should().HaveCount(1);

        // Also run the same inputs through intersect to verify the results differ
        var intersectResponse = await _fixture.Client.PostAsync(
            "/rest/services/geometry/intersect",
            new StringContent(body, Encoding.UTF8, "application/json"));

        intersectResponse.Be200Ok();
        var intersectContent = await intersectResponse.Content.ReadAsStringAsync();
        var intersectResult = JsonSerializer.Deserialize(intersectContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        intersectResult.Should().NotBeNull();
        intersectResult!.Geometries.Should().HaveCount(1);

        // Clip (envelope-based) and intersect (full geometry) should produce different geometries
        // because the clip uses the rectangular envelope of the triangle, not the triangle itself
        var clipGeom = clipResult.Geometries![0].GetRawText();
        var intersectGeom = intersectResult.Geometries![0].GetRawText();
        clipGeom.Should().NotBe(intersectGeom,
            "clip should use the envelope of the clip geometry, producing a different result than intersect");
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("GET /rest/services/geometry/clip")]
    public async Task Clip_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/clip?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/geometry/difference")]
    public async Task Difference_PostValidRequest_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/difference",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("GET /rest/services/geometry/difference")]
    public async Task Difference_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/difference?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/geometry/area")]
    public async Task Area_PostValidRequest_ReturnsAreaValues()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[10,0],[10,10],[0,10],[0,0]]]}
                ]
            },
            "sr": "3857",
            "areaUnit": "esriSquareMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/area",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreaResponse);
        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/geometry/area")]
    public async Task Area_GeographicInput_ReturnsSquareMeters()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "sr": "4326",
            "areaUnit": "esriSquareMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/area",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreaResponse);

        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(1_000_000_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("GET /rest/services/geometry/area")]
    public async Task Area_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/area?sr=4326");
        response.Be400BadRequest();
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/geometry/length")]
    public async Task Length_PostValidRequest_ReturnsLengthValues()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[3,4]]]}
                ]
            },
            "sr": "3857",
            "lengthUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/length",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);
        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/geometry/length")]
    public async Task Length_GeographicInput_ReturnsMeters()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[1,0]]]}
                ]
            },
            "sr": "4326",
            "lengthUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/geometry/length",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);

        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(100_000d);
        result.Lengths![0].Should().BeLessThan(120_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("GET /rest/services/geometry/length")]
    public async Task Length_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/geometry/length?sr=4326");
        response.Be400BadRequest();
    }
}
