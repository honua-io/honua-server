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

// The fixture's callers are not signed in: one request may build a quarter of
// Limits:Edits:MaxPayloadSize of result geometry (6.25 MiB with the default limits), and send
// at most half of the Geometry Service byte bound.
[Protocol(TestProtocols.GeometryService)]
[Collection("Database.GeoServicesRaster")]
public sealed class GeometryServiceResultLimitTests(GeometryServiceComponentFixture fixture)
{
    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_InputTimesDistancesAboveTheResultBudget_Returns400()
    {
        // The inputs times the distances is past the budget for a caller who is not signed in.
        var vertices = string.Join(',', Enumerable.Range(0, 500).Select(index => $"[{index},{index % 2}]"));
        var distances = string.Join(',', Enumerable.Range(1, 1_000));
        using var content = Json($$"""
        {
            "geometries": {"geometryType": "esriGeometryPolyline", "geometries": [{"paths": [[{{vertices}}]]}]},
            "inSR": "3857",
            "distances": "{{distances}}",
            "unit": "esriMeters"
        }
        """);

        using var response = await fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        await response.AssertGeoServicesErrorAsync(400);
    }

    [IntegrationTest]
    [Operation(Operations.Buffer)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/buffer")]
    public async Task Buffer_ManyDistancesOfAPoint_ReturnsEveryRing()
    {
        var distances = string.Join(',', Enumerable.Range(1, 100));
        using var content = Json($$"""
        {
            "geometries": {"geometryType": "esriGeometryPoint", "geometries": [{"x": 0, "y": 0}]},
            "inSR": "3857",
            "distances": "{{distances}}",
            "unit": "esriMeters"
        }
        """);

        using var response = await fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/buffer", content);

        response.Be200Ok();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("geometries").GetArrayLength().Should().Be(100);
    }

    [IntegrationTest]
    [Operation(Operations.Densify)]
    [Endpoint("POST /rest/services/Utilities/Geometry/GeometryServer/densify")]
    public async Task Densify_BodyLargerThanTheShareOfACallerNotSignedIn_Returns413()
    {
        // Under the server's payload limit, over half of the Geometry Service byte bound.
        using var content = Json($$"""{"f": "json", "padding": "{{new string('x', 17_000_000)}}"}""");

        using var response = await fixture.Client.PostAsync("/rest/services/Utilities/Geometry/GeometryServer/densify", content);

        await response.AssertGeoServicesErrorAsync(413);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
