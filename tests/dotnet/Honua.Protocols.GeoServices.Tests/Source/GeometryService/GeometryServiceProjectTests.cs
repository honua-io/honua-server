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

[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceProjectTests : IClassFixture<WebAppFixture>
{
    private readonly WebAppFixture _fixture;

    public GeometryServiceProjectTests(WebAppFixture fixture)
    {
        _fixture = fixture;
    }

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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

    [IntegrationTheory]
    [InlineData(false, 4267, 4269, -100.00041558862637, 39.999996883362)]
    [InlineData(false, 4269, 4267, -99.99958442682077, 40.00000311609789)]
    [InlineData(true, 4267, 4269, -100.00040583667015, 40.00000589472259)]
    [InlineData(true, 4269, 4267, -99.99959418404879, 39.999994102939)]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_DefaultNad27Nad83_PreservesOrdinatesAndMatchesIndependentReference(
        bool includeNadconGrid, int sourceSrid, int targetSrid, double expectedX, double expectedY)
    {
        // Independent pyproj 3.7.2 / PROJ 9.5.1 references with network disabled:
        // no grids (Helmert -8,159,175), and the pinned NOAA NADCON grid.
        // CI's external PostGIS 16 fixture includes a legacy conus grid; isolate
        // operation availability instead of assuming every base image is grid-free.
        await using var datumDatabase = new DatumGridPostgresFixture();
        await datumDatabase.InitializeAsync(includeNadconGrid);
        var fixture = datumDatabase.ConfigureGeometryService(new WebAppFixture());
        await fixture.InitializeAsync();
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["f"] = "json",
                ["inSR"] = sourceSrid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["outSR"] = targetSrid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["geometries"] = """{"geometryType":"esriGeometryPoint","geometries":[{"x":-100,"y":40,"z":12,"m":7}]}"""
            });
            using var response = await fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, body);
            using var document = JsonDocument.Parse(body);
            document.RootElement.GetProperty("geometryType").GetString().Should().Be("esriGeometryPoint");
            var geometry = document.RootElement.GetProperty("geometries").EnumerateArray().Single();
            geometry.GetProperty("x").GetDouble().Should().BeApproximately(expectedX, 2e-9);
            geometry.GetProperty("y").GetDouble().Should().BeApproximately(expectedY, 2e-9);
            geometry.GetProperty("z").GetDouble().Should().Be(12);
            geometry.GetProperty("m").GetDouble().Should().Be(7);
            geometry.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(targetSrid);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        await response.AssertGeoServicesErrorAsync(400);
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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

        await response.AssertGeoServicesErrorAsync(400);
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

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
    public async Task Project_WithDatumTransformationWkid_AppliesSelectedPipeline()
    {
        // WKID 108001 (NAD_1983_To_WGS_1984_1) is the Esri default for NAD83 (4269) ->
        // WGS84 (4326); its catalog pipeline is the exact-identity +proj=noop. Supplying
        // the WKID must be honored (200) and yield coordinates within datum tolerance.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0, "spatialReference": {"wkid": 4269}}]
            },
            "inSR": "4269",
            "outSR": "4326",
            "datumTransformation": 108001
        }
        """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);

        var geom = result.Geometries![0];
        geom.TryGetProperty("x", out var x).Should().BeTrue();
        geom.TryGetProperty("y", out var y).Should().BeTrue();
        x.GetDouble().Should().BeApproximately(-100.0, 0.01);
        y.GetDouble().Should().BeApproximately(40.0, 0.01);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_WithEsriTransformationParameter_AppliesSelectedPipeline()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0}]
            },
            "inSR": 4269,
            "outSR": 4326,
            "transformation": 108001,
            "transformForward": true
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = document.RootElement.GetProperty("geometries")[0];
        point.GetProperty("x").GetDouble().Should().BeApproximately(-100.0, 1e-9);
        point.GetProperty("y").GetDouble().Should().BeApproximately(40.0, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_WithSingleWkidTransformationObject_AppliesSelectedPipeline()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0}]
            },
            "inSR": 4269,
            "outSR": 4326,
            "transformation": {"wkid": 108001}
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = document.RootElement.GetProperty("geometries")[0];
        point.GetProperty("x").GetDouble().Should().BeApproximately(-100.0, 1e-9);
        point.GetProperty("y").GetDouble().Should().BeApproximately(40.0, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_ProjectedSourceTransformation_ResolvesAgainstGeodeticBase()
    {
        // pyproj 3.7.2 EPSG:26910 -> EPSG:4326 reference: the UTM zone 10N
        // central-meridian point (500000, 0) maps to (-123, 0).
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 500000.0, "y": 0.0}]
            },
            "inSR": 26910,
            "outSR": 4326,
            "transformation": 108001,
            "transformForward": true
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = document.RootElement.GetProperty("geometries")[0];
        point.GetProperty("x").GetDouble().Should().BeApproximately(-123.0, 1e-8);
        point.GetProperty("y").GetDouble().Should().BeApproximately(0.0, 1e-8);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_NonIdentityTransformationPipeline_UsesSelectedNadconGrid()
    {
        // Keep the independent pyproj NADCON reference and supply its required grid
        // in a dedicated real PostGIS fixture. The base fixture remains grid-free.
        await using var datumDatabase = new DatumGridPostgresFixture();
        await datumDatabase.InitializeAsync();
        var fixture = datumDatabase.ConfigureGeometryService(new WebAppFixture());
        await fixture.InitializeAsync();
        try
        {
            var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0}]
            },
            "inSR": 4267,
            "outSR": 4269,
            "transformation": 1241,
            "transformForward": true
        }
        """;

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await fixture.Client.PostAsync(
                "/rest/services/Utilities/Geometry/GeometryServer/project", content);

            response.Be200Ok();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var point = document.RootElement.GetProperty("geometries")[0];
            point.GetProperty("x").GetDouble().Should().BeApproximately(-100.00040583667015, 2e-9);
            point.GetProperty("y").GetDouble().Should().BeApproximately(40.00000589472259, 2e-9);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_WithUnsupportedEsriTransformation_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0}]
            },
            "inSR": 4269,
            "outSR": 4326,
            "transformation": 999999,
            "transformForward": true
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_ReverseTransformationDirection_MatchesPyprojIdentity()
    {
        // pyproj 3.7.2 Transformer.from_crs(4326, 4269, always_xy=True) returns
        // (-100, 40). WKID 108001 is the corresponding Esri null transformation.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -100.0, "y": 40.0}]
            },
            "inSR": 4326,
            "outSR": 4269,
            "transformation": 108001,
            "transformForward": false
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var point = document.RootElement.GetProperty("geometries")[0];
        point.GetProperty("x").GetDouble().Should().BeApproximately(-100.0, 1e-9);
        point.GetProperty("y").GetDouble().Should().BeApproximately(40.0, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_CircularCurve_PreservesZAndM()
    {
        // pyproj 3.7.2 EPSG:4326 -> EPSG:3857 reference endpoints:
        // (1,0) -> (111319.49079327357,0), (0,1) -> (0,111325.1428663851).
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPolyline",
                "geometries": [{
                    "hasZ": true,
                    "hasM": true,
                    "curvePaths": [[[1,0,3,4], {"c":[[0,1,5,6],[0,0]]}]]
                }]
            },
            "inSR": 4326,
            "outSR": 3857
        }
        """;

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await _fixture.Client.PostAsync(
            "/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var output = document.RootElement.GetProperty("geometries")[0];
        output.GetProperty("hasZ").GetBoolean().Should().BeTrue();
        output.GetProperty("hasM").GetBoolean().Should().BeTrue();

        var vertices = output.GetProperty("paths")[0].EnumerateArray().ToArray();
        vertices.Should().OnlyContain(vertex => vertex.GetArrayLength() == 4);
        vertices[0][0].GetDouble().Should().BeApproximately(111319.49079327357, 1e-6);
        vertices[0][1].GetDouble().Should().BeApproximately(0.0, 1e-9);
        vertices[0][2].GetDouble().Should().BeApproximately(3.0, 1e-9);
        vertices[0][3].GetDouble().Should().BeApproximately(4.0, 1e-9);
        vertices[^1][0].GetDouble().Should().BeApproximately(0.0, 1e-9);
        vertices[^1][1].GetDouble().Should().BeApproximately(111325.1428663851, 1e-6);
        vertices[^1][2].GetDouble().Should().BeApproximately(5.0, 1e-9);
        vertices[^1][3].GetDouble().Should().BeApproximately(6.0, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_WithUnsupportedDatumTransformationWkid_Returns400()
    {
        // WKID 108001 does not connect the 4326 -> 3857 pair, so an explicit request for it
        // must be rejected rather than silently substituted.
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0, "spatialReference": {"wkid": 4326}}]
            },
            "inSR": "4326",
            "outSR": "3857",
            "datumTransformation": 108001
        }
        """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    public async Task Project_WithMalformedDatumTransformation_Returns400()
    {
        var body = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0, "spatialReference": {"wkid": 4269}}]
            },
            "inSR": "4269",
            "outSR": "4326",
            "datumTransformation": "not-a-wkid"
        }
        """;
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        await response.AssertGeoServicesErrorAsync(400);
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
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/project", content);

        response.Be200Ok();

        var responseContent = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<GeometryServiceResponse>(
            responseContent, GeometryServiceJsonContext.Default.GeometryServiceResponse);

        result.Should().NotBeNull();
        result!.Geometries.Should().HaveCount(1);
    }
}
