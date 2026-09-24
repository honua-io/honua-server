// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Linq;
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
public sealed class GeometryServiceBufferTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public GeometryServiceBufferTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_DocumentedCommaSeparatedPointShorthand_ReturnsPolygon()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/buffer" +
            "?geometries=-117,34&inSR=4326&outSR=4326&distances=1000&unit=esriMeters&geodesic=true&f=json");

        response.Be200Ok();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            await response.Content.ReadAsStringAsync(), GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result!.GeometryType.Should().Be("esriGeometryPolygon");
        result.Geometries.Should().ContainSingle();
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);
        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var ring = result.Geometries![0].GetProperty("rings")[0];
        var xValues = ring.EnumerateArray().Select(point => point[0].GetDouble()).ToList();
        var minX = xValues.Min();
        var maxX = xValues.Max();

        var widthDegrees = maxX - minX;
        widthDegrees.Should().BeGreaterThan(0.001);
        widthDegrees.Should().BeLessThan(0.1,
            "a 1km buffer in EPSG:4326 should be a small angular distance, not hundreds of degrees");
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GeographicPointAt60N_KeepsGroundRadius()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 60}]
            },
            "inSR": "4326",
            "outSR": "4326",
            "distances": "1000",
            "unit": "esriMeters",
            "geodesic": "false"
        }
        """;

        var ring = await PostBufferRingAsync(body);
        ring.Should().NotBeEmpty();
        foreach (var vertex in ring)
        {
            var ground = VincentyMeters(0d, 60d, vertex.X, vertex.Y);
            ground.Should().BeApproximately(1000d, 10d);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GeographicLineFrom40NTo50N_KeepsRadiusAtBothEnds()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [{"paths": [[[0, 40], [0, 50]]]}]
            },
            "inSR": "4326",
            "outSR": "4326",
            "distances": "1000",
            "unit": "esriMeters",
            "geodesic": "false"
        }
        """;

        var ring = await PostBufferRingAsync(body);
        var southY = ring.Min(point => point.Y);
        var northY = ring.Max(point => point.Y);
        var south = ring.First(point => point.Y == southY);
        var north = ring.First(point => point.Y == northY);
        var southOffset = MeridianOffsetDegrees(40d, 1000d);
        var northOffset = MeridianOffsetDegrees(50d, 1000d);

        south.Y.Should().BeApproximately(40d - southOffset, southOffset * 0.01d);
        north.Y.Should().BeApproximately(50d + northOffset, northOffset * 0.01d);
        VincentyMeters(0d, 40d, south.X, south.Y).Should().BeApproximately(1000d, 10d);
        VincentyMeters(0d, 50d, north.X, north.Y).Should().BeApproximately(1000d, 10d);
    }

    private async Task<List<(double X, double Y)>> PostBufferRingAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);
        response.Be200Ok();
        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);
        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
        return result.Geometries![0].GetProperty("rings")[0].EnumerateArray()
            .Select(point => (point[0].GetDouble(), point[1].GetDouble()))
            .ToList();
    }

    private static double MeridianOffsetDegrees(double latitudeDegrees, double meters)
    {
        const double semiMajor = 6_378_137d;
        const double eccentricitySquared = 6.69437999014e-3;
        var phi = latitudeDegrees * Math.PI / 180d;
        var sine = Math.Sin(phi);
        var denominator = 1d - eccentricitySquared * sine * sine;
        var meridional = semiMajor * (1d - eccentricitySquared) / Math.Pow(denominator, 1.5);
        return meters / meridional * 180d / Math.PI;
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        await response.AssertGeoServicesErrorAsync(400);
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        await response.AssertGeoServicesErrorAsync(400);

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        await response.AssertGeoServicesErrorAsync(400);
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

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/buffer",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);

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

        using var requestContent = new StringContent(requestBody, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/buffer",
            requestContent);

        await response.AssertGeoServicesErrorAsync(400);

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

        await response.AssertGeoServicesErrorAsync(400);
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }
}
