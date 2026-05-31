// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

/// <summary>
/// Integration coverage for the measure/edit/analysis GeometryServer operations
/// (#1301): distance, relation, densify, convexHull, generalize, labelPoints.
/// These are the operations ArcGIS Pro and the ArcGIS API for Python invoke most
/// frequently and which previously returned 404.
/// </summary>
[Protocol(TestProtocols.GeometryService)]
[Collection("Database")]
public sealed class GeometryServiceMeasureAnalysisTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- distance ---

    [IntegrationTest]
    [Operation(Operations.Distance)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/distance")]
    public async Task Distance_PostPlanarPoints_ReturnsClosestDistance()
    {
        var body = """
        {
            "geometry1": {"x": 0, "y": 0},
            "geometry2": {"x": 3, "y": 4},
            "sr": "3857",
            "distanceUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse);
        result.Should().NotBeNull();
        result!.Distance.Should().BeApproximately(5.0, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Distance)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/distance")]
    public async Task Distance_GetWithValidParameters_ReturnsDistance()
    {
        var geometry1 = Uri.EscapeDataString("""{"x":0,"y":0}""");
        var geometry2 = Uri.EscapeDataString("""{"x":0,"y":1}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/distance?geometry1={geometry1}&geometry2={geometry2}&sr=4326&geodesic=true&distanceUnit=esriMeters");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse);
        result.Should().NotBeNull();
        // One degree of latitude is roughly 110-112 km.
        result!.Distance.Should().BeGreaterThan(110_000d).And.BeLessThan(112_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Distance)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/distance")]
    public async Task Distance_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance?sr=4326");
        response.Be400BadRequest();
    }

    // #1308: ArcGIS clients wrap geometry1/geometry2 as
    // {"geometryType":"...","geometry":{...}}. The parser must unwrap them.
    [IntegrationTest]
    [Operation(Operations.Distance)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/distance")]
    public async Task Distance_PostEsriWrappedGeometries_ReturnsClosestDistance()
    {
        var body = """
        {
            "geometry1": {"geometryType": "esriGeometryPoint", "geometry": {"x": 0, "y": 0}},
            "geometry2": {"geometryType": "esriGeometryPoint", "geometry": {"x": 3, "y": 4}},
            "sr": "3857",
            "distanceUnit": "esriMeters"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse);
        result.Should().NotBeNull();
        result!.Distance.Should().BeApproximately(5.0, 0.001);
    }

    // --- relation ---

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_PostIntersection_ReturnsMatchingPairs()
    {
        var body = """
        {
            "geometries1": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
                ]
            },
            "geometries2": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]},
                    {"rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]}
                ]
            },
            "sr": "4326",
            "relation": "esriGeometryRelationIntersection"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/relation",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceRelationResponse);
        result.Should().NotBeNull();
        result!.Relations.Should().ContainSingle();
        result.Relations![0].Geometry1Index.Should().Be(0);
        result.Relations[0].Geometry2Index.Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_PostRelationPattern_UsesDe9imParam()
    {
        var body = """
        {
            "geometries1": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
                ]
            },
            "geometries2": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]}
                ]
            },
            "sr": "4326",
            "relation": "esriGeometryRelationRelation",
            "relationParam": "T********"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/relation",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceRelationResponse);
        result.Should().NotBeNull();
        result!.Relations.Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_GetMissingRelation_Returns400()
    {
        var geometries1 = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]}""");
        var geometries2 = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[1,1],[3,1],[3,3],[1,3],[1,1]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/relation?geometries1={geometries1}&geometries2={geometries2}&sr=4326");

        response.Be400BadRequest();
    }

    // --- densify ---

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_PostPolyline_AddsVertices()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[10,0]]]}
                ]
            },
            "sr": "3857",
            "maxSegmentLength": 2.0
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        // The 10-unit segment split at maxSegmentLength=2 must add interior vertices.
        var paths = result.Geometries![0];
        paths.TryGetProperty("paths", out var pathsElement).Should().BeTrue();
        pathsElement[0].GetArrayLength().Should().BeGreaterThan(2);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_GetMissingMaxSegmentLength_Returns400()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[10,0]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/densify?geometries={geometries}&sr=3857");

        response.Be400BadRequest();
    }

    // --- convexHull ---

    [IntegrationTest]
    [Operation(Operations.ConvexHull)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/convexHull")]
    public async Task ConvexHull_PostMultiplePoints_ReturnsHullPolygon()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": 0, "y": 0},
                    {"x": 4, "y": 0},
                    {"x": 4, "y": 4},
                    {"x": 0, "y": 4},
                    {"x": 2, "y": 2}
                ]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/convexHull",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse);
        result.Should().NotBeNull();
        // The hull of the five points is a rectangle (rings present).
        result!.Geometry.TryGetProperty("rings", out var rings).Should().BeTrue();
        rings.GetArrayLength().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.ConvexHull)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/convexHull")]
    public async Task ConvexHull_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/convexHull?sr=4326");
        response.Be400BadRequest();
    }

    // --- generalize ---

    [IntegrationTest]
    [Operation(Operations.Generalize)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/generalize")]
    public async Task Generalize_PostPolyline_RemovesNearCollinearVertices()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[5,0.01],[10,0]]]}
                ]
            },
            "sr": "3857",
            "maxDeviation": 1.0
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/generalize",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        var paths = result.Geometries![0];
        paths.TryGetProperty("paths", out var pathsElement).Should().BeTrue();
        // The near-collinear middle vertex should be dropped, leaving 2 points.
        pathsElement[0].GetArrayLength().Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Generalize)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/generalize")]
    public async Task Generalize_GetMissingMaxDeviation_Returns400()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[10,0]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/generalize?geometries={geometries}&sr=3857");

        response.Be400BadRequest();
    }

    // --- labelPoints ---

    [IntegrationTest]
    [Operation(Operations.LabelPoints)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/labelPoints")]
    public async Task LabelPoints_PostPolygons_ReturnsInteriorPoints()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[10,0],[10,10],[0,10],[0,0]]]}
            ],
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/labelPoints",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("x", out _).Should().BeTrue();
        result.Geometries[0].TryGetProperty("y", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.LabelPoints)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/labelPoints")]
    public async Task LabelPoints_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/labelPoints?sr=4326");
        response.Be400BadRequest();
    }
}
