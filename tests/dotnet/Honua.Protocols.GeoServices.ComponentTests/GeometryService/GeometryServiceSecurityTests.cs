// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceSecurityTests(GeometryServiceComponentFixture fixture)
{
    private const string GeometryServer = "/rest/services/Utilities/Geometry/GeometryServer";

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    [InlineData("4326; DROP TABLE spatial_ref_sys--")]
    [InlineData("4326' OR '1'='1")]
    [InlineData("1 UNION SELECT 4326--")]
    [InlineData("""GEOGCS["WGS 84"]; DROP TABLE spatial_ref_sys--""")]
    [InlineData("""GEOGCS["WGS 84' OR '1'='1"]""")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Project_HostileInSrInjection_Returns400(string hostileInSr)
    {
        var body = JsonSerializer.Serialize(new
        {
            geometries = new
            {
                geometryType = "esriGeometryPoint",
                geometries = new[] { new { x = -122.4194, y = 37.7749 } }
            },
            inSR = hostileInSr,
            outSR = "3857"
        });

        await AssertHostilePostRejectedAsync("project", body);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    [InlineData("""{"wkid":"4326; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"wkt":"'; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"wkt":"WGS84'; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"wkt":"GEOGCS[\"WGS 84\"]; DROP TABLE spatial_ref_sys--"}""")]
    public async Task Project_HostileJsonInSrInjection_Returns400(string hostileInSrJson)
    {
        var body = $$"""
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749}]
            },
            "inSR": {{hostileInSrJson}},
            "outSR": "3857"
        }
        """;

        await AssertHostilePostRejectedAsync("project", body);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    [InlineData("3857; DROP TABLE spatial_ref_sys--")]
    [InlineData("3857' OR '1'='1")]
    [InlineData("""GEOGCS["WGS 84"]; SELECT pg_sleep(5)--""")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Project_HostileOutSrInjection_Returns400(string hostileOutSr)
    {
        var body = JsonSerializer.Serialize(new
        {
            geometries = new
            {
                geometryType = "esriGeometryPoint",
                geometries = new[] { new { x = -122.4194, y = 37.7749 } }
            },
            inSR = "4326",
            outSR = hostileOutSr
        });

        await AssertHostilePostRejectedAsync("project", body);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Project)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/project")]
    [InlineData("4326; DROP TABLE spatial_ref_sys--", "3857")]
    [InlineData("4326", "3857; DROP TABLE spatial_ref_sys--")]
    public async Task Project_GetHostileSpatialReferenceInjection_Returns400(string inSr, string outSr)
    {
        var geometries = Uri.EscapeDataString(
            """{"geometryType":"esriGeometryPoint","geometries":[{"x":-122.4194,"y":37.7749}]}""");
        var url = $"{GeometryServer}/project?f=json&geometries={geometries}"
            + $"&inSR={Uri.EscapeDataString(inSr)}&outSR={Uri.EscapeDataString(outSr)}";

        using var response = await fixture.Client.GetAsync(url);

        await AssertRejectedWithoutLeakAsync(response);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Project)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/project")]
    [InlineData("""{"wkid":"4326; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"latestWkid":"4326' OR '1'='1"}""")]
    [InlineData("""{"wkid":-1}""")]
    [InlineData("""{"wkt":"'; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"wkt":"WGS84'; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"wkt":"GEOGCS[\"WGS 84\"]; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("""{"name":"EPSG:4326; DROP TABLE spatial_ref_sys--"}""")]
    [InlineData("\"4326; DROP TABLE spatial_ref_sys--\"")]
    public async Task Project_HostileGeometrySpatialReferenceInjection_Returns400(string hostileSpatialReferenceJson)
    {
        var body = $$"""
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": -122.4194, "y": 37.7749, "spatialReference": {{hostileSpatialReferenceJson}}}]
            },
            "inSR": "4326",
            "outSR": "3857"
        }
        """;

        await AssertHostilePostRejectedAsync("project", body);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    [InlineData("esriMeters'; DROP TABLE spatial_ref_sys--")]
    [InlineData("9001 UNION SELECT 1--")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Buffer_HostileUnitInjection_Returns400(string hostileUnit)
    {
        var body = JsonSerializer.Serialize(new
        {
            geometries = new
            {
                geometryType = "esriGeometryPoint",
                geometries = new[] { new { x = 0, y = 0 } }
            },
            inSR = "3857",
            distances = "100",
            unit = hostileUnit
        });

        await AssertHostilePostRejectedAsync("buffer", body);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTest]
    [SecurityTest]
    [Operation(Operations.Buffer)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_GetHostileUnitInjection_Returns400()
    {
        var unit = Uri.EscapeDataString("esriMeters'; DROP TABLE spatial_ref_sys--");
        var url = $"{GeometryServer}/buffer?f=json&geometries=0,0&inSR=3857&distances=100&unit={unit}";

        using var response = await fixture.Client.GetAsync(url);

        await AssertRejectedWithoutLeakAsync(response);
        await AssertSpatialRefSysIntactAsync();
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Distance)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/distance")]
    [InlineData("esriMeters'; DROP TABLE spatial_ref_sys--")]
    [InlineData("9001; SELECT pg_sleep(5)--")]
    public async Task Distance_HostileUnitInjection_Returns400(string hostileUnit)
    {
        var body = JsonSerializer.Serialize(new
        {
            geometry1 = new { x = 0, y = 0 },
            geometry2 = new { x = 10, y = 0 },
            sr = "3857",
            distanceUnit = hostileUnit
        });

        await AssertHostilePostRejectedAsync("distance", body);
    }

    [IntegrationTheory]
    [SecurityTest]
    [Operation(Operations.Area)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/areasAndLengths")]
    [InlineData("esriSquareMeters'; DROP TABLE spatial_ref_sys--", "esriMeters")]
    [InlineData("""{"areaUnit":"esriSquareMeters'; DROP TABLE spatial_ref_sys--"}""", "esriMeters")]
    [InlineData("esriSquareMeters", "esriMeters'; DROP TABLE spatial_ref_sys--")]
    public async Task AreasAndLengths_HostileUnitInjection_Returns400(string areaUnit, string lengthUnit)
    {
        var body = $$"""
        {
            "polygons": [{"rings": [[[0,0],[10,0],[10,10],[0,10],[0,0]]]}],
            "sr": "3857",
            "areaUnit": {{JsonSerializer.Serialize(areaUnit)}},
            "lengthUnit": {{JsonSerializer.Serialize(lengthUnit)}}
        }
        """;

        await AssertHostilePostRejectedAsync("areasAndLengths", body);
    }

    private async Task AssertHostilePostRejectedAsync(string operation, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync($"{GeometryServer}/{operation}", content);

        await AssertRejectedWithoutLeakAsync(response);
    }

    private static async Task AssertRejectedWithoutLeakAsync(HttpResponseMessage response)
    {
        await response.AssertGeoServicesErrorAsync(400);
        var text = await response.Content.ReadAsStringAsync();
        text.Should().NotContain("DROP TABLE");
        text.Should().NotContain("pg_sleep");
        text.Should().NotContain("Npgsql");
        text.Should().NotContain("System.Text.Json");
    }

    private async Task AssertSpatialRefSysIntactAsync()
    {
        const string validBody = """
        {
            "geometries": {
                "geometryType": "esriGeometryPoint",
                "geometries": [{"x": 0, "y": 0}]
            },
            "inSR": "4326",
            "outSR": "3857"
        }
        """;
        using var content = new StringContent(validBody, Encoding.UTF8, "application/json");
        using var response = await fixture.Client.PostAsync($"{GeometryServer}/project", content);

        response.Be200Ok();
    }
}
