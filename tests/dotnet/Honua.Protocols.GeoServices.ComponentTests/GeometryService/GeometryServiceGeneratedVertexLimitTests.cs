// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GeometryService;

// The fixture's callers are not signed in, so one request may generate as many vertices as one
// geometry may hold: 50,000 with the default limits.
[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceGeneratedVertexLimitTests(GeometryServiceComponentFixture fixture)
{
    private const string GeometryServer = "/rest/services/Utilities/Geometry/GeometryServer";

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_AtTheGeometryVertexLimit_ReturnsTheDensifiedLine()
    {
        using var response = await PostAsync("densify", DensifyBody("""{"paths": [[[0,0],[50000,0]]]}"""));

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("geometries")[0].GetProperty("paths")[0].GetArrayLength().Should().Be(50_001);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_OneGeometryAboveTheVertexLimit_Returns400()
    {
        using var response = await PostAsync("densify", DensifyBody("""{"paths": [[[0,0],[50001,0]]]}"""));

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_GeometriesTogetherAboveTheRequestLimit_Returns400()
    {
        var geometries = """{"paths": [[[0,0],[30000,0]]]}, {"paths": [[[0,10],[30000,10]]]}""";

        using var response = await PostAsync("densify", DensifyBody(geometries));

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("GET /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_GetAboveTheVertexLimit_Returns400()
    {
        var geometries = Uri.EscapeDataString(
            """{"geometryType":"esriGeometryPolyline","geometries":[{"paths":[[[0,0],[16000000,0]]]}]}""");

        using var response = await fixture.Client.GetAsync(
            $"{GeometryServer}/densify?f=json&sr=3857&maxSegmentLength=1&geometries={geometries}");

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Length)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/lengths")]
    public async Task Lengths_ShapePreservingSamplesAboveTheVertexLimit_Returns400()
    {
        // Shape-preserving measurement samples every 10 km: 14 crossings of 40,000 km each.
        var vertices = string.Join(',', Enumerable.Range(0, 15).Select(index => $"[{(index % 2 == 0 ? -20000000 : 20000000)},{index}]"));
        var body = $$"""
        {
            "polylines": [{"paths": [[{{vertices}}]]}],
            "sr": "3857",
            "lengthUnit": "esriMeters",
            "calculationType": "preserveShape"
        }
        """;

        using var response = await PostAsync("lengths", body);

        await response.AssertGeoServicesErrorAsync(400);
    }

    private static string DensifyBody(string geometries) => $$"""
        {
            "geometries": {"geometryType": "esriGeometryPolyline", "geometries": [{{geometries}}]},
            "sr": "3857",
            "maxSegmentLength": 1.0
        }
        """;

    private async Task<HttpResponseMessage> PostAsync(string operation, string body)
    {
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        return await fixture.Client.PostAsync($"{GeometryServer}/{operation}", content);
    }
}
