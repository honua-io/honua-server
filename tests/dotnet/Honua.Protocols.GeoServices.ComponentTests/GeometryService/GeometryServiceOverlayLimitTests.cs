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
