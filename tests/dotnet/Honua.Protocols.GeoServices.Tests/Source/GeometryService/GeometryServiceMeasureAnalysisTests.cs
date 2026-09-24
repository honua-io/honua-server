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
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceMeasureAnalysisTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public GeometryServiceMeasureAnalysisTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance",
            requestContent);

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
        await response.AssertGeoServicesErrorAsync(400);
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse);
        result.Should().NotBeNull();
        result!.Distance.Should().BeApproximately(5.0, 0.001);
    }

    [IntegrationTest]
    [Operation(Operations.Distance)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/distance")]
    public async Task Distance_GeodesicAntimeridianChord_UsesSpheroidalNearestPoint()
    {
        // The lon/lat chord from 170° to -170° is the long way around. Planar nearest-point
        // distance from (180, 0) is about 1,112 km. On the spheroid the edge is the 20° arc
        // across the antimeridian, and the point lies on it.
        var body = """
        {
            "geometry1": {"x": 180, "y": 0},
            "geometry2": {"paths": [[[170, 0], [-170, 0]]]},
            "sr": "4326",
            "geodesic": "GEODESIC",
            "distanceUnit": "esriMeters"
        }
        """;

        var geodesic = await PostDistanceAsync(body.Replace("GEODESIC", "true", StringComparison.Ordinal));
        var planar = await PostDistanceAsync(body.Replace("GEODESIC", "false", StringComparison.Ordinal));

        geodesic.Should().BeLessThan(1d);
        planar.Should().BeGreaterThan(1_000_000d);
        (planar - geodesic).Should().BeGreaterThan(1_000d);
    }

    private async Task<double> PostDistanceAsync(string body)
    {
        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/distance",
            requestContent);
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceDistanceResponse);
        result.Should().NotBeNull();
        return result!.Distance;
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/relation",
            requestContent);

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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/relation",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceRelationResponse);
        result.Should().NotBeNull();
        result!.Relations.Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_CornerTouchingParcels_PointTouchMatchesLineTouchDoesNot()
    {
        // Two squares meeting only at the corner (1,1): boundary∩boundary is a single point (#2742).
        const string square1 = """{"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";
        const string square2 = """{"rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]}""";

        (await RelationMatchCountAsync(square1, square2, "esriGeometryRelationPointTouch"))
            .Should().Be(1, "corner contact is a 0-dimensional boundary touch");
        (await RelationMatchCountAsync(square1, square2, "esriGeometryRelationLineTouch"))
            .Should().Be(0, "a corner touch is not a 1-dimensional (line) boundary touch");
    }

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_EdgeSharingParcels_LineTouchMatchesPointTouchDoesNot()
    {
        // Two squares sharing the full edge x=1 from y=0 to y=1: boundary∩boundary is a line (#2742).
        const string square1 = """{"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}""";
        const string square2 = """{"rings": [[[1,0],[2,0],[2,1],[1,1],[1,0]]]}""";

        (await RelationMatchCountAsync(square1, square2, "esriGeometryRelationLineTouch"))
            .Should().Be(1, "a shared edge is a 1-dimensional boundary touch");
        (await RelationMatchCountAsync(square1, square2, "esriGeometryRelationPointTouch"))
            .Should().Be(0, "a shared edge is not a 0-dimensional (point) boundary touch");
    }

    private async Task<int> RelationMatchCountAsync(string ring1, string ring2, string relation)
    {
        var body = $$"""
        {
            "geometries1": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [ {{ring1}} ]
            },
            "geometries2": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [ {{ring2}} ]
            },
            "sr": "4326",
            "relation": "{{relation}}"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/relation",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceRelationResponse);
        result.Should().NotBeNull();
        return result!.Relations?.Length ?? 0;
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

        await response.AssertGeoServicesErrorAsync(400);
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

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
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_EllipticArc_AcceptsEsriTrueCurve()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [{
                    "curvePaths": [[[10,0], {"a":[[0,5],[0,0],1,0,0,10,0.5]}]]
                }]
            },
            "sr": 3857,
            "maxSegmentLength": 1000,
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        var path = document.RootElement.GetProperty("geometries")[0].GetProperty("paths")[0];
        path.GetArrayLength().Should().BeGreaterThan(2);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_FiveElementArc_AcceptsIssueReproduction()
    {
        // Preserve the exact payload from #4117. Esri documents four elements for a
        // center-form circle and seven for an ellipse, but clients in the probe sent a
        // fifth trailing rotation value; treating it as the circular form is compatible
        // with that probe shape while the documented seven-element ellipse stays covered.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [{
                    "curvePaths": [[[0,0], {"a":[[10,0],[5,0],0,0,1.0]}]]
                }]
            },
            "sr": 3857,
            "maxSegmentLength": 1000,
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        document.RootElement.GetProperty("geometries")[0].GetProperty("paths")[0]
            .GetArrayLength().Should().BeGreaterThan(2);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_GetMissingMaxSegmentLength_Returns400()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[10,0]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/densify?geometries={geometries}&sr=3857");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_TinyMaxSegmentLengthOverLargeExtent_Returns400WithoutOom()
    {
        // #2064: a 2,000,000-unit polyline at maxSegmentLength=0.001 would densify to ~2e9
        // interpolated coordinates synchronously and OOM the host. The vertex-count guard must
        // reject it with a 400 (fast, allocation-free) instead of attempting the densify.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[2000000,0]]]}
                ]
            },
            "sr": "3857",
            "maxSegmentLength": 0.001
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_ReasonableMaxSegmentLengthOverLargeExtent_StillSucceeds()
    {
        // The cap must not reject legitimate densify requests: a 2,000,000-unit segment at
        // maxSegmentLength=1000 yields ~2000 vertices, far under the per-geometry cap.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[2000000,0]]]}
                ]
            },
            "sr": "3857",
            "maxSegmentLength": 1000.0
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        var paths = result.Geometries![0];
        paths.TryGetProperty("paths", out var pathsElement).Should().BeTrue();
        pathsElement[0].GetArrayLength().Should().BeGreaterThan(2);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_GeodesicAt60N_KeepsGroundSpacingAndLeavesTheParallel()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0, 60], [10, 60]]]}
                ]
            },
            "sr": "4326",
            "geodesic": true,
            "maxSegmentLength": 1000,
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var path = document.RootElement.GetProperty("geometries")[0].GetProperty("paths")[0];
        path.GetArrayLength().Should().BeGreaterThan(2);

        var coordinates = path.EnumerateArray()
            .Select(point => (X: point[0].GetDouble(), Y: point[1].GetDouble()))
            .ToArray();
        var offChord = coordinates.Any(point => Math.Abs(point.Y - 60d) * 111_320d > 100d);
        offChord.Should().BeTrue("a geodesic at 60°N bows off the straight parallel by more than 100 m");

        var lengths = new double[coordinates.Length - 1];
        for (var i = 1; i < coordinates.Length; i++)
        {
            lengths[i - 1] = VincentyMeters(coordinates[i - 1].X, coordinates[i - 1].Y, coordinates[i].X, coordinates[i].Y);
        }

        lengths.Max().Should().BeLessThan(1_010d);
        lengths.Where(length => length > 100d).Should().OnlyContain(length => Math.Abs(length - 1_000d) / 1_000d < 0.01d);
    }

    private static double VincentyMeters(double lon1, double lat1, double lon2, double lat2)
    {
        const double semiMajor = 6_378_137d;
        const double flattening = 1d / 298.257223563d;
        var semiMinor = semiMajor * (1d - flattening);
        var phi1 = lat1 * Math.PI / 180d;
        var phi2 = lat2 * Math.PI / 180d;
        var longitudeDelta = (lon2 - lon1) * Math.PI / 180d;
        var u1 = Math.Atan((1d - flattening) * Math.Tan(phi1));
        var u2 = Math.Atan((1d - flattening) * Math.Tan(phi2));
        var sinU1 = Math.Sin(u1);
        var cosU1 = Math.Cos(u1);
        var sinU2 = Math.Sin(u2);
        var cosU2 = Math.Cos(u2);
        var lambda = longitudeDelta;
        double sinSigma = 0;
        double cosSigma = 0;
        double sigma = 0;
        double cosSqAlpha = 0;
        double cos2SigmaM = 0;
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var sinLambda = Math.Sin(lambda);
            var cosLambda = Math.Cos(lambda);
            sinSigma = Math.Sqrt(
                Math.Pow(cosU2 * sinLambda, 2) +
                Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosLambda, 2));
            if (sinSigma == 0)
            {
                return 0;
            }

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = 1d - sinAlpha * sinAlpha;
            cos2SigmaM = cosSqAlpha == 0d ? 0d : cosSigma - 2d * sinU1 * sinU2 / cosSqAlpha;
            var c = flattening / 16d * cosSqAlpha * (2d + flattening * (4d - 3d * cosSqAlpha));
            var previous = lambda;
            lambda = longitudeDelta + (1d - c) * flattening * sinAlpha
                * (sigma + c * sinSigma * (cos2SigmaM + c * cosSigma * (-1d + 2d * cos2SigmaM * cos2SigmaM)));
            if (Math.Abs(lambda - previous) < 1e-12)
            {
                break;
            }
        }

        var uSq = cosSqAlpha * (semiMajor * semiMajor - semiMinor * semiMinor) / (semiMinor * semiMinor);
        var aCoeff = 1d + uSq / 16384d * (4096d + uSq * (-768d + uSq * (320d - 175d * uSq)));
        var bCoeff = uSq / 1024d * (256d + uSq * (-128d + uSq * (74d - 47d * uSq)));
        var deltaSigma = bCoeff * sinSigma * (cos2SigmaM + bCoeff / 4d
            * (cosSigma * (-1d + 2d * cos2SigmaM * cos2SigmaM)
                - bCoeff / 6d * cos2SigmaM * (-3d + 4d * sinSigma * sinSigma) * (-3d + 4d * cos2SigmaM * cos2SigmaM)));
        return semiMinor * aCoeff * (sigma - deltaSigma);
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/convexHull",
            requestContent);

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
        await response.AssertGeoServicesErrorAsync(400);
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/generalize",
            requestContent);

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

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Generalize)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/generalize")]
    public async Task Generalize_DeviationUnitConvertsToSpatialReferenceUnits()
    {
        var body = """
        {
            "geometries": {"geometryType":"esriGeometryPolyline","geometries":[
                {"paths":[[[0,0],[0.01,0.005],[0.02,0]]]}
            ]},
            "sr": 4326,
            "maxDeviation": 100,
            "deviationUnit": "esriMeters"
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/generalize",
            content);

        response.Be200Ok();
        var result = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(), GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result!.Geometries![0].GetProperty("paths")[0].GetArrayLength().Should().Be(3,
            "100 metres is much smaller than the middle vertex's angular deviation");
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/labelPoints",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLabelPointsResponse);
        result.Should().NotBeNull();
        result!.LabelPoints.Should().HaveCount(1);
        result.LabelPoints![0].TryGetProperty("x", out _).Should().BeTrue();
        result.LabelPoints[0].TryGetProperty("y", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.LabelPoints)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/labelPoints")]
    public async Task LabelPoints_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/labelPoints?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }
}
