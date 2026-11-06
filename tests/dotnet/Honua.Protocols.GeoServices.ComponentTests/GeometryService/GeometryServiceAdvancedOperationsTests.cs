// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Protocols.GeoServices.GeometryService.Models;
using Honua.Infrastructure.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceAdvancedOperationsTests
{
    private readonly GeometryServiceComponentFixture _fixture;

    public GeometryServiceAdvancedOperationsTests(GeometryServiceComponentFixture fixture)
    {
        _fixture = fixture;
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/intersect?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse);
        result.Should().NotBeNull();
        result!.Geometry.TryGetProperty("rings", out _).Should().BeTrue("the Esri union operation returns a single geometry");
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/union");
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_PostValidRequest_ReturnsGeometry()
    {
        // Regression for #1443: clip clips each input geometry to the Esri `envelope`
        // parameter (not a `geometry` operand). A request that omits envelope must fail,
        // and a request supplying envelope must succeed.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "envelope": {
                "xmin": 1, "ymin": 1, "xmax": 2, "ymax": 2,
                "spatialReference": {"wkid": 4326}
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/clip",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_WithEnvelope_ClipsToEnvelopeBounds()
    {
        // Regression for #1443. The target polygon is (0,0)-(4,4); the clip envelope is
        // (1,1)-(3,3). The clipped result is the (1,1)-(3,3) window, which differs from the
        // unclipped target, proving the envelope parameter is honored.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[4,0],[4,4],[0,4],[0,0]]]}
                ]
            },
            "envelope": {
                "xmin": 1, "ymin": 1, "xmax": 3, "ymax": 3,
                "spatialReference": {"wkid": 4326}
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var clipResponse = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/clip",
            requestContent);

        clipResponse.Be200Ok();
        var clipContent = await clipResponse.Content.ReadAsStringAsync();
        var clipResult = JsonSerializer.Deserialize(clipContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        clipResult.Should().NotBeNull();
        clipResult!.Geometries.Should().HaveCount(1);

        // Every clipped ring coordinate must fall inside the (1,1)-(3,3) envelope, proving
        // the envelope actually constrained the output away from the original 4x4 target.
        var clipGeom = clipResult.Geometries![0];
        clipGeom.TryGetProperty("rings", out var rings).Should().BeTrue();
        foreach (var ring in rings.EnumerateArray())
        {
            foreach (var point in ring.EnumerateArray())
            {
                var px = point[0].GetDouble();
                var py = point[1].GetDouble();
                px.Should().BeInRange(1, 3);
                py.Should().BeInRange(1, 3);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_MissingEnvelope_Returns400()
    {
        // Regression for #1443: clip requires the `envelope` parameter, not `geometry`.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]}
                ]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/clip",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Clip)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/clip")]
    public async Task Clip_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/clip?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
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

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/difference?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }

    // #1308: ArcGIS clients wrap the single operating geometry as
    // {"geometryType":"...","geometry":{...}}. The parser must unwrap it.

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_PostEsriWrappedGeometry_ReturnsGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
                ]
            },
            "geometry": {
                "geometryType": "esriGeometryPolygon",
                "geometry": {"rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]}
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_PostEsriWrappedGeometry_ReturnsGeometry()
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
                "geometryType": "esriGeometryPolygon",
                "geometry": {"rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]}
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_PostValidRequest_ReturnsAreaValues()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[0,10],[10,10],[10,0],[0,0]]]}
            ],
            "sr": "3857",
            "lengthUnit": "esriMeters",
            "areaUnit": "esriSquareMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);
        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(0);
        result.Lengths![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_GeographicInput_ReturnsSquareMeters()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters",
            "areaUnit": "esriSquareMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);

        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeLessThan(0);
        result.Areas[0].Should().BeLessThan(-1_000_000_000d);
        result.Lengths![0].Should().BeGreaterThan(400_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_PreserveShapeCalculationType_ReturnsGeodeticMeasurements()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[0,1],[1,1],[1,0],[0,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "9036",
            "areaUnit": {"areaUnit": "esriSquareKilometers"},
            "calculationType": "preserveShape"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);

        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        result.Lengths.Should().HaveCount(1);
        result.Areas![0].Should().BeGreaterThan(12_000d);
        result.Lengths![0].Should().BeGreaterThan(440d);
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_PreserveShapeOnCoarseWebMercatorPolygon_DiffersFromVertexGeodesic()
    {
        // A 10° by 1° quadrilateral around 60°N. preserveShape samples the straight
        // Web Mercator edges, then measures that densified shape on the spheroid.
        // The ground area must agree with a geodesic measure of the same densified
        // shape within 1%, not the Mercator-scaled planar area.
        const double radius = 6_378_137d;
        var ring = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"""
            [[[{MercatorX(0, radius):G17},{MercatorY(60, radius):G17}],[{MercatorX(10, radius):G17},{MercatorY(60, radius):G17}],[{MercatorX(10, radius):G17},{MercatorY(61, radius):G17}],[{MercatorX(0, radius):G17},{MercatorY(61, radius):G17}],[{MercatorX(0, radius):G17},{MercatorY(60, radius):G17}]]]
            """);
        var densified = DensifyWebMercatorQuadrilateral(radius, stepsPerEdge: 40);

        var preserveShape = Math.Abs(await PostAreaSquareMetersAsync(ring, "preserveShape"));
        var densifiedGeodesic = Math.Abs(await PostAreaSquareMetersAsync(densified, "geodesic"));

        densifiedGeodesic.Should().BeGreaterThan(1e10);
        Math.Abs(preserveShape - densifiedGeodesic).Should().BeLessThan(densifiedGeodesic * 0.01d);
    }

    private async Task<double> PostAreaSquareMetersAsync(string ring, string calculationType)
    {
        var body = $$"""
        {
            "polygons": [
                {"rings": {{ring}}}
            ],
            "sr": "3857",
            "lengthUnit": "esriMeters",
            "areaUnit": "esriSquareMeters",
            "calculationType": "{{calculationType}}"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            requestContent);
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceAreasAndLengthsResponse);
        result.Should().NotBeNull();
        result!.Areas.Should().HaveCount(1);
        return result.Areas![0];
    }

    private static double MercatorX(double longitudeDegrees, double radius)
        => longitudeDegrees * Math.PI / 180d * radius;

    private static double MercatorY(double latitudeDegrees, double radius)
        => Math.Log(Math.Tan(Math.PI / 4d + latitudeDegrees * Math.PI / 360d)) * radius;

    private static string DensifyWebMercatorQuadrilateral(double radius, int stepsPerEdge)
    {
        var corners = new (double X, double Y)[]
        {
            (MercatorX(0, radius), MercatorY(60, radius)),
            (MercatorX(10, radius), MercatorY(60, radius)),
            (MercatorX(10, radius), MercatorY(61, radius)),
            (MercatorX(0, radius), MercatorY(61, radius))
        };

        var builder = new StringBuilder();
        builder.Append("[[");
        var first = true;
        for (var edge = 0; edge < corners.Length; edge++)
        {
            var start = corners[edge];
            var end = corners[(edge + 1) % corners.Length];
            for (var step = 0; step < stepsPerEdge; step++)
            {
                var t = step / (double)stepsPerEdge;
                AppendMercatorVertex(builder, ref first, start.X + ((end.X - start.X) * t), start.Y + ((end.Y - start.Y) * t));
            }
        }

        AppendMercatorVertex(builder, ref first, corners[0].X, corners[0].Y);
        builder.Append("]]");
        return builder.ToString();
    }

    private static void AppendMercatorVertex(StringBuilder builder, ref bool first, double x, double y)
    {
        if (!first)
        {
            builder.Append(',');
        }

        first = false;
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"[{x:G17},{y:G17}]");
    }

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_PostValidRequest_ReturnsLengthValues()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "sr": "3857",
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);
        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GeographicInput_ReturnsMeters()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[1,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);

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
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GeodesicCalculationType_UsesGeodeticMeasurement()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[1,0]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters",
            "calculationType": "geodesic"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);

        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        result.Lengths![0].Should().BeGreaterThan(111_250d);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_PlanarAt60N_MatchesEquatorialEsriScale()
    {
        // One degree of longitude at 60°N. calculationType=planar keeps today's
        // Esri number: both axes times the equatorial radius, with no cos(latitude).
        const double equatorialRadiusMeters = 6_371_000d;
        var expected = equatorialRadiusMeters * Math.PI / 180d;

        var planar = await PostLengthMetersAsync(0d, 60d, 1d, 60d, "planar");

        planar.Should().BeApproximately(expected, 1d);
        planar.Should().BeGreaterThan(100_000d);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GroundAt60N_MatchesSpheroid()
    {
        // A 10° longitude span at 60°N. calculationType=ground is the ground result:
        // within 1% of the WGS 84 spheroid, not the equatorial planar scale.
        var ground = await PostLengthMetersAsync(0d, 60d, 10d, 60d, "ground");
        var spheroid = VincentyMeters(0d, 60d, 10d, 60d);

        spheroid.Should().BeGreaterThan(500_000d);
        Math.Abs(ground - spheroid).Should().BeLessThan(spheroid * 0.01d);
    }

    private async Task<double> PostLengthMetersAsync(
        double x1,
        double y1,
        double x2,
        double y2,
        string calculationType)
    {
        var body = string.Create(System.Globalization.CultureInfo.InvariantCulture, $$"""
        {
            "polylines": [
                {"paths": [[[{{x1:G17}},{{y1:G17}}],[{{x2:G17}},{{y2:G17}}]]]}
            ],
            "sr": "4326",
            "lengthUnit": "esriMeters",
            "calculationType": "{{calculationType}}"
        }
        """);

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);
        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceLengthResponse);
        result.Should().NotBeNull();
        result!.Lengths.Should().HaveCount(1);
        return result.Lengths![0];
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
            if (sinSigma <= 0)
            {
                return 0;
            }

            cosSigma = sinU1 * sinU2 + cosU1 * cosU2 * cosLambda;
            sigma = Math.Atan2(sinSigma, cosSigma);
            var sinAlpha = cosU1 * cosU2 * sinLambda / sinSigma;
            cosSqAlpha = Math.Max(0d, 1d - sinAlpha * sinAlpha);
            cos2SigmaM = cosSqAlpha <= 0d ? 0d : cosSigma - 2d * sinU1 * sinU2 / cosSqAlpha;
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

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_GetMissingParameters_Returns400()
    {
        var response = await _fixture.Client.GetAsync("/rest/services/Utilities/Geometry/GeometryServer/lengths?sr=4326");
        await response.AssertGeoServicesErrorAsync(400);
    }

    // --- Intersect: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]}""");
        var geometry = Uri.EscapeDataString("""{"rings":[[[1,1],[3,1],[3,3],[1,3],[1,1]]]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/intersect?geometries={geometries}&geometry={geometry}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_PointDisjointFromPolygon_Returns200WithEmptyPoint()
    {
        // Point-vs-polygon is a legal Esri intersect input combination (the "geometry" operand's
        // dimension must be >= the "geometries" elements' dimension), and this point is disjoint
        // from the polygon, so the result is an empty point of the request type — Esri serializes
        // empty points with null ordinates ({"x":null,"y":null}) — not a 400 (#2742).
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [
                    {"x": 5, "y": 5}
                ]
            },
            "geometry": {
                "rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.GeometryType.Should().Be("esriGeometryPoint");
        result.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("x", out var x).Should().BeTrue();
        x.ValueKind.Should().Be(JsonValueKind.Null);
        result.Geometries[0].TryGetProperty("y", out var y).Should().BeTrue();
        y.ValueKind.Should().Be(JsonValueKind.Null);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[2,0],[2,2],[0,2],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            }
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": "not-an-array"}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[3,1],[3,3],[1,3],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    // --- Union: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[1,0],[1,1],[0,1],[0,0]]]},{"rings":[[[1,0],[2,0],[2,1],[1,1],[1,0]]]}]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/union?geometries={geometries}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse);
        result.Should().NotBeNull();
        result!.Geometry.TryGetProperty("rings", out _).Should().BeTrue("the Esri union operation returns a single geometry");
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_SingleGeometry_ReturnsSameGeometry()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceGeometryResponse);
        result.Should().NotBeNull();
        result!.Geometry.TryGetProperty("rings", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            }
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": "invalid"}
                ]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/union",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    // --- Difference: additional coverage ---

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_GetWithValidParameters_ReturnsGeometry()
    {
        var geometries = Uri.EscapeDataString("""{"geometryType":"esriGeometryPolygon","geometries":[{"rings":[[[0,0],[3,0],[3,3],[0,3],[0,0]]]}]}""");
        var geometry = Uri.EscapeDataString("""{"rings":[[[1,1],[2,1],[2,2],[1,2],[1,1]]]}""");

        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Utilities/Geometry/GeometryServer/difference?geometries={geometries}&geometry={geometry}&sr=4326");

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_NoOverlap_ReturnsOriginal()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_MissingSpatialReference_Returns400()
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
            }
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_MalformedGeometry_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": 12345}
                ]
            },
            "geometry": {
                "rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    // --- Area/Length: missing SR coverage ---

    [IntegrationTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    public async Task Area_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "polygons": [
                {"rings": [[[0,0],[10,0],[10,10],[0,10],[0,0]]]}
            ],
            "areaUnit": "esriSquareMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/areasAndLengths",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_MissingSpatialReference_Returns400()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "lengthUnit": "esriMeters"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Length_InvalidCalculationType_ReturnsStandardGeoServicesError()
    {
        var body = """
        {
            "polylines": [
                {"paths": [[[0,0],[3,4]]]}
            ],
            "sr": "4326",
            "calculationType": "unsupported"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/lengths",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);

        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ApiErrorResponse>(content);
        result.Should().NotBeNull();
        result!.Error.Message.Should().Be("Bad Request");
        result.Error.Details.Should().NotBeNull();
        result.Error.Details!.Should().Contain(detail => detail.Contains("calculationType", StringComparison.Ordinal));
    }

    // --- Empty results (#2742): disjoint intersect / full difference must be 200 + empty geometry ---

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_DisjointPolygons_Returns200WithEmptyGeometry()
    {
        // PostGIS returns GEOMETRYCOLLECTION EMPTY for a disjoint intersection. That must render
        // as an empty Esri polygon ({"rings":[]}), not a 400.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[0,0],[1,0],[1,1],[0,1],[0,0]]]}
                ]
            },
            "geometry": {
                "rings": [[[10,10],[11,10],[11,11],[10,11],[10,10]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/intersect",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("rings", out var rings).Should().BeTrue();
        rings.GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Difference)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/difference")]
    public async Task Difference_SubtractCoveringGeometry_Returns200WithEmptyGeometry()
    {
        // Subtracting a geometry that fully covers the input yields an empty result. That must be
        // 200 + empty Esri polygon rather than a 400.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolygon",
                "geometries": [
                    {"rings": [[[1,1],[2,1],[2,2],[1,2],[1,1]]]}
                ]
            },
            "geometry": {
                "rings": [[[0,0],[3,0],[3,3],[0,3],[0,0]]]
            },
            "sr": "4326"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/difference",
            requestContent);

        response.Be200Ok();
        var content = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(content, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        result.Geometries![0].TryGetProperty("rings", out var rings).Should().BeTrue();
        rings.GetArrayLength().Should().Be(0);
    }

    // --- Unknown unit token (#2742): must be 400, not silently defaulted to a meters multiplier ---

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_UnknownLengthUnit_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [
                    {"paths": [[[0,0],[10,0]]]}
                ]
            },
            "sr": "4326",
            "maxSegmentLength": 1,
            "geodesic": true,
            "lengthUnit": "esriBananas"
        }
        """;

        using var requestContent = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/densify",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);
    }
}
