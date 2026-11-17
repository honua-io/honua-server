// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// The fixture's callers are not signed in, so one overlay may examine 500,000 segment pairs that
// could cross with the default limits. Rows and columns of 750 lines cross 562,500 times: each
// operation is refused before it runs, with a message that names the cause.
[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceOverlayLimitTests(GeometryServiceComponentFixture fixture)
{
    private const string GeometryServer = "/rest/services/Utilities/Geometry/GeometryServer";
    private const int GridLines = 750;
    private const string RefusalStart = "The geometries cross each other";

    private static readonly string _rows = Polyline(Enumerable.Range(0, GridLines).Select(i => $"[[-1,{i}],[{GridLines},{i}]]"));
    private static readonly string _columns = Polyline(Enumerable.Range(0, GridLines).Select(i => $"[[{i},-1],[{i},{GridLines}]]"));

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_InputsThatCrossTooOften_Returns400BeforeTheOverlay()
    {
        var body = $$"""
        {"geometries": {"geometryType": "esriGeometryPolyline", "geometries": [{{_rows}}]}, "geometry": {{_columns}}, "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("intersect", body));
    }

    [IntegrationTest]
    [Operation(Operations.Union)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/union")]
    public async Task Union_InputsThatCrossTooOften_Returns400BeforeTheOverlay()
    {
        var body = $$"""
        {"geometries": {"geometryType": "esriGeometryPolyline", "geometries": [{{_rows}}, {{_columns}}]}, "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("union", body));
    }

    [IntegrationTest]
    [Operation(Operations.Cut)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/cut")]
    public async Task Cut_CutterThatCrossesTooOften_Returns400BeforeTheOverlay()
    {
        var body = $$"""
        {"target": {"geometryType": "esriGeometryPolyline", "geometries": [{{_rows}}]}, "cutter": {{_columns}}, "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("cut", body));
    }

    [IntegrationTest]
    [Operation(Operations.AutoComplete)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/autoComplete")]
    public async Task AutoComplete_LinesThatCrossTooOften_Returns400BeforeTheOverlay()
    {
        var body = $$"""
        {"polygons": [], "polylines": [{{_rows}}, {{_columns}}], "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("autoComplete", body));
    }

    [IntegrationTest]
    [Operation(Operations.Relation)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/relation")]
    public async Task Relation_InputsThatCrossTooOften_Returns400BeforeTheOverlay()
    {
        var body = $$"""
        {
            "geometries1": {"geometryType": "esriGeometryPolyline", "geometries": [{{_rows}}]},
            "geometries2": {"geometryType": "esriGeometryPolyline", "geometries": [{{_columns}}]},
            "relation": "esriGeometryRelationIntersection",
            "sr": "3857"
        }
        """;

        await AssertRefusedAsync(await PostAsync("relation", body));
    }

    [IntegrationTest]
    [Operation(Operations.Generalize)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/generalize")]
    public async Task Generalize_RingThatCrossesItselfTooOften_Returns400BeforeTheRepair()
    {
        // A star of 1,101 points joined 550 apart: every edge crosses about 1,100 others, and the
        // simplifier repairs the invalid ring it keeps.
        var body = $$"""
        {"geometries": {"geometryType": "esriGeometryPolygon", "geometries": [{{Star()}}]}, "maxDeviation": 0.000001, "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("generalize", body));
    }

    [IntegrationTest]
    [Operation(Operations.TrimExtend)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/trimExtend")]
    public async Task TrimExtend_LinesThatCrossTooOften_Returns400BeforeTrimming()
    {
        var body = $$"""
        {"polylines": [{{_rows}}], "trimExtendTo": {{_columns}}, "extendHow": 0, "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("trimExtend", body));
    }

    [IntegrationTest]
    [Operation(Operations.Offset)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/offset")]
    public async Task Offset_CurvesThatCrossTooOften_Returns400()
    {
        // 1,000 thin teeth two units apart offset by 60: the teeth cross nothing, the curves drawn
        // around them cross their neighbours'.
        var body = $$"""
        {"geometries": {"geometryType": "esriGeometryPolygon", "geometries": [{{Comb()}}]}, "offsetDistance": 60, "offsetUnit": "esriMeters", "sr": "3857"}
        """;

        await AssertRefusedAsync(await PostAsync("offset", body));
    }

    [IntegrationTest]
    [Operation(Operations.Intersect)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/intersect")]
    public async Task Intersect_OverlappingDetailedPolygons_ReturnsTheOverlay()
    {
        var body = $$"""
        {"geometries": {"geometryType": "esriGeometryPolygon", "geometries": [{{Circle(0)}}]}, "geometry": {{Circle(500)}}, "sr": "3857"}
        """;

        using var response = await PostAsync("intersect", body);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("geometries")[0].GetProperty("rings").GetArrayLength().Should().BePositive();
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync();
            await response.AssertGeoServicesErrorAsync(400);
            using var document = JsonDocument.Parse(text);
            document.RootElement.GetProperty("error").GetProperty("message").GetString()
                .Should().StartWith(RefusalStart);
        }
    }

    private static string Polyline(IEnumerable<string> paths) => $$"""{"paths": [{{string.Join(',', paths)}}]}""";

    private static string Star()
    {
        const int points = 1_101;
        var vertices = Enumerable.Range(0, points + 1).Select(i =>
        {
            var angle = -2 * Math.PI * (i * 550 % points) / points;
            return string.Create(CultureInfo.InvariantCulture, $"[{1_000 * Math.Cos(angle):F6},{1_000 * Math.Sin(angle):F6}]");
        });
        return $$"""{"rings": [[{{string.Join(',', vertices)}}]]}""";
    }

    // A base strip with 1,000 teeth one unit wide, two apart and 100 high, as a clockwise ring.
    private static string Comb()
    {
        const int teeth = 1_000;
        var points = new List<string> { "[0,-10]", "[0,0]" };
        for (var i = 0; i < teeth; i++)
        {
            var x = 2 * i;
            points.AddRange([$"[{x},100]", $"[{x + 1},100]", $"[{x + 1},0]", $"[{x + 2},0]"]);
        }

        points.AddRange([$"[{2 * teeth},-10]", "[0,-10]"]);
        return $$"""{"rings": [[{{string.Join(',', points)}}]]}""";
    }

    // A 5,000-vertex circle of radius 1,000 around (centerX, 0).
    private static string Circle(double centerX)
    {
        const int vertices = 5_000;
        var points = Enumerable.Range(0, vertices + 1).Select(i =>
        {
            var angle = -2 * Math.PI * (i % vertices) / vertices;
            return string.Create(
                CultureInfo.InvariantCulture,
                $"[{centerX + (1_000 * Math.Cos(angle)):F6},{1_000 * Math.Sin(angle):F6}]");
        });
        return $$"""{"rings": [[{{string.Join(',', points)}}]]}""";
    }

    private async Task<HttpResponseMessage> PostAsync(string operation, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await fixture.Client.PostAsync($"{GeometryServer}/{operation}", content);
    }
}
