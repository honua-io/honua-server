// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// The NAServer service resource and analysis-layer resources (#5035). ArcGIS Pro's
/// Catalog pane and <c>arcpy.nax</c> read these before they will address a routing
/// service; until they existed every Esri routing client stopped at
/// "The requested operation or resource was not found".
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerMetadataEndpointTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public NAServerMetadataEndpointTests(NAServerEndpointTestsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer")]
    public async Task ServiceResource_ListsOneAnalysisLayerPerSupportedSolver()
    {
        using var response = await _fixture.Client.GetAsync("/rest/services/Routing/NAServer?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        Names(root, "routeLayers").Should().Equal("Route");
        Names(root, "serviceAreaLayers").Should().Equal("ServiceArea");
        Names(root, "closestFacilityLayers").Should().Equal("ClosestFacility");
        Names(root, "odCostMatrixLayers").Should().Equal("ODCostMatrix");
        Names(root, "locationAllocationLayers").Should().Equal("LocationAllocation");
        root.GetProperty("serviceDescription").GetString().Should().Contain("Routing");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer")]
    public async Task ServiceResource_AcceptsTheFormPostArcGisProIssues()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "pjson")]);
        using var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("\n", "f=pjson is indented");
        using var document = JsonDocument.Parse(body);
        Names(document.RootElement, "routeLayers").Should().Equal("Route");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{layerName}")]
    public async Task RouteLayer_DescribesImpedanceTravelModesNetworkAndInputs()
    {
        using var response = await _fixture.Client.GetAsync("/rest/services/Routing/NAServer/Route?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("layerName").GetString().Should().Be("Route");
        root.GetProperty("layerType").GetString().Should().Be("esriNAServerRouteLayer");
        root.GetProperty("impedance").GetString().Should().Be("TravelTime");
        root.GetProperty("supportsDirections").GetBoolean().Should().BeTrue();
        root.GetProperty("outputSpatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);

        var modes = root.GetProperty("supportedTravelModes").EnumerateArray().ToArray();
        modes.Select(m => m.GetProperty("name").GetString()).Should().Equal(["driving", "walking"],
            "metadata must include every mode supported by the active provider");
        // As on a real ArcGIS Server layer, the layer's modes carry an ordinal itemId and
        // defaultTravelMode names one of them; the 16-character ids live in GetTravelModes.
        var defaultMode = root.GetProperty("defaultTravelMode").GetString();
        modes.Select(m => m.GetProperty("itemId").GetString()).Should().Contain(defaultMode);
        modes[0].GetProperty("impedanceAttributeName").GetString().Should().Be("TravelTime");
        modes[0].GetProperty("type").GetString().Should().Be("AUTOMOBILE");
        foreach (var mode in modes)
        {
            await AssertTravelModeCanSolveAsync(_fixture.Client, mode.GetRawText());
        }
        root.GetProperty("locateSettings").GetProperty("default").GetProperty("sources").GetArrayLength().Should().BeGreaterThan(0);

        var attributes = root.GetProperty("networkDataset").GetProperty("networkAttributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).ToArray();
        attributes.Should().Contain(["TravelTime", "Kilometers"]);
        root.GetProperty("networkDataset").GetProperty("state").GetString().Should().Be("esriNDSStateBuilt");

        var classes = root.GetProperty("networkClasses").EnumerateArray().ToArray();
        classes.Select(c => c.GetProperty("className").GetString())
            .Should().Contain(["Stops", "Barriers", "PolylineBarriers", "PolygonBarriers"]);
        var stops = classes.Single(c => c.GetProperty("className").GetString() == "Stops");
        stops.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("fieldName").GetString())
            .Should().Contain(["Shape", "Name", "Sequence"], "input classes describe their fields the way ArcGIS Server does");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{layerName}")]
    public async Task ServiceAreaLayer_IsAddressableByFormPost()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        using var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/ServiceArea", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("layerType").GetString().Should().Be("esriNAServerServiceAreaLayer");
        root.GetProperty("defaultBreaks").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("networkClasses").EnumerateArray()
            .Select(c => c.GetProperty("className").GetString()).Should().Contain("Facilities");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{layerName}")]
    public async Task UnknownLayer_IsNotFound()
    {
        using var response = await _fixture.Client.GetAsync("/rest/services/Routing/NAServer/VehicleRouting?f=json");

        // GeoServices REST errors travel as HTTP 200 envelopes carrying the Esri code.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(404);
        document.RootElement.GetProperty("error").GetProperty("message").GetString().Should().NotBeNullOrEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer")]
    public async Task ServiceResource_RejectsAnUnsupportedFormat()
    {
        using var response = await _fixture.Client.GetAsync("/rest/services/Routing/NAServer?f=html");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
    }

    private static string?[] Names(JsonElement root, string property)
        => root.GetProperty(property).EnumerateArray().Select(e => e.GetString()).ToArray();

    internal static async Task AssertTravelModeCanSolveAsync(HttpClient client, string travelMode)
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            new KeyValuePair<string, string>("travelMode", travelMode),
            new KeyValuePair<string, string>("returnRoutes", "true"),
        ]);
        using var response = await client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "a client must be able to submit the advertised travel mode unchanged: {0}", document.RootElement.GetRawText());
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }
}
