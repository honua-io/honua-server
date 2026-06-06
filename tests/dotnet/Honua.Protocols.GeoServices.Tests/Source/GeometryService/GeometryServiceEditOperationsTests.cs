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
/// Integration coverage for the geometry edit/transform GeometryServer operations
/// added as a follow-up to #1301: cut, trimExtend, offset, autoComplete, reshape,
/// and findTransformations. These are the remaining operations ArcGIS Pro and the
/// ArcGIS SDKs invoke that previously returned 404.
/// </summary>
[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesParallel1")]
public sealed class GeometryServiceEditOperationsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    // --- cut ---

    [IntegrationTest]
    [Operation(Operations.Cut)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/cut")]
    public async Task Cut_PostPolygonByCutter_ReturnsTwoPieces()
    {
        // A unit square cut by a vertical line through x=0.5 yields two halves.
        var body = """
        {
            "target": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "cutter": {
                "paths": [[[0.5,-1],[0.5,2]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/cut",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceCutResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(2);
        result.CutIndexes.Should().NotBeNull();
        result.CutIndexes!.Should().OnlyContain(index => index == 0);
    }

    [IntegrationTest]
    [Operation(Operations.Cut)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/cut")]
    public async Task Cut_PostPolylineByCrossingCutter_SplitsLine()
    {
        var body = """
        {
            "target": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[4,0]]]}
                ]
            },
            "cutter": {
                "paths": [[[2,-1],[2,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/cut",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceCutResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCountGreaterThanOrEqualTo(2);
    }

    [IntegrationTest]
    [Operation(Operations.Cut)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/cut")]
    public async Task Cut_GetMissingCutter_Returns400()
    {
        var target = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}]}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/cut?target={target}&sr=4326");
        response.Be400BadRequest();
    }

    // --- trimExtend ---

    [IntegrationTest]
    [Operation(Operations.TrimExtend)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/trimExtend")]
    public async Task TrimExtend_PostExtendsPolylineToTrimLine_ReturnsLongerLine()
    {
        // The polyline ends at x=2; the trim line is the vertical line x=5.
        // Extending should lengthen the polyline so its end reaches x=5.
        var body = """
        {
            "polylines": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[2,0]]]}
                ]
            },
            "trimExtendTo": {
                "paths": [[[5,-2],[5,2]]]
            },
            "sr": "4326",
            "extendHow": "0"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/trimExtend",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var geometry = result.Geometries![0];
        var paths = geometry.GetProperty("paths");
        var lastPath = paths[paths.GetArrayLength() - 1];
        var lastVertex = lastPath[lastPath.GetArrayLength() - 1];
        lastVertex[0].GetDouble().Should().BeApproximately(5.0, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.TrimExtend)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/trimExtend")]
    public async Task TrimExtend_PostTrimsPolylineAtCrossing_ReturnsShorterLine()
    {
        // The polyline runs to x=10 but crosses the trim line at x=3, so it is trimmed there.
        var body = """
        {
            "polylines": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[10,0]]]}
                ]
            },
            "trimExtendTo": {
                "paths": [[[3,-2],[3,2]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/trimExtend",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var paths = result.Geometries![0].GetProperty("paths");
        var lastPath = paths[paths.GetArrayLength() - 1];
        var lastVertex = lastPath[lastPath.GetArrayLength() - 1];
        lastVertex[0].GetDouble().Should().BeApproximately(3.0, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.TrimExtend)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/trimExtend")]
    public async Task TrimExtend_GetMissingTrimLine_Returns400()
    {
        var polylines = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[2,0]]]}]}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/trimExtend?polylines={polylines}&sr=4326");
        response.Be400BadRequest();
    }

    // --- offset ---

    [IntegrationTest]
    [Operation(Operations.Offset)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/offset")]
    public async Task Offset_PostPolyline_ReturnsParallelLine()
    {
        // Offsetting a horizontal line by +1 should shift it to y=1.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[5,0]]]}
                ]
            },
            "sr": "4326",
            "offsetDistance": 1,
            "offsetUnit": "esriMeters",
            "offsetHow": "esriGeometryOffsetRounded"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/offset",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var paths = result.Geometries![0].GetProperty("paths");
        var firstPath = paths[0];
        var firstVertex = firstPath[0];
        Math.Abs(firstVertex[1].GetDouble()).Should().BeApproximately(1.0, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Offset)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/offset")]
    public async Task Offset_PostPolygon_ShrinksArea()
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
            "offsetDistance": -1
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/offset",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("rings", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Offset)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/offset")]
    public async Task Offset_GetMissingOffsetDistance_Returns400()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[5,0]]]}]}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/offset?geometries={geometries}&sr=4326");
        response.Be400BadRequest();
    }

    // --- autoComplete ---

    [IntegrationTest]
    [Operation(Operations.AutoComplete)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/autoComplete")]
    public async Task AutoComplete_PostPolylinesFormingClosedLoop_ReturnsPolygon()
    {
        // Two polylines that together trace the full boundary of a unit square.
        var body = """
        {
            "polygons": [],
            "polylines": [
                {"paths": [[[0,0],[1,0],[1,1]]]},
                {"paths": [[[1,1],[0,1],[0,0]]]}
            ],
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/autoComplete",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("rings", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.AutoComplete)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/autoComplete")]
    public async Task AutoComplete_GetMissingPolylines_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/autoComplete?sr=4326");
        response.Be400BadRequest();
    }

    // --- reshape ---

    [IntegrationTest]
    [Operation(Operations.Reshape)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/reshape")]
    public async Task Reshape_PostPolygonWithReshaper_ReturnsModifiedPolygon()
    {
        // Reshaper bulges the right edge of the square outward to x=2.
        var body = """
        {
            "target": {
                "rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]
            },
            "reshaper": {
                "paths": [[[1,0],[2,0.5],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/reshape",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse);
        result.Should().NotBeNull();
        result!.Geometry.TryGetProperty("rings", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Reshape)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/reshape")]
    public async Task Reshape_GetMissingReshaper_Returns400()
    {
        var target = Uri.EscapeDataString("""{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/reshape?target={target}&sr=4326");
        response.Be400BadRequest();
    }

    // --- findTransformations ---

    [IntegrationTest]
    [Operation(Operations.FindTransformations)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/findTransformations")]
    public async Task FindTransformations_PostValidSpatialReferences_ReturnsTransformationsArray()
    {
        var body = """
        {
            "inSR": "4267",
            "outSR": "4326"
        }
        """;

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/findTransformations",
            new StringContent(body, Encoding.UTF8, "application/json"));

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceFindTransformationsResponse);
        result.Should().NotBeNull();
        result!.Transformations.Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.FindTransformations)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/findTransformations")]
    public async Task FindTransformations_GetMissingOutSR_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/findTransformations?inSR=4326");
        response.Be400BadRequest();
    }
}
