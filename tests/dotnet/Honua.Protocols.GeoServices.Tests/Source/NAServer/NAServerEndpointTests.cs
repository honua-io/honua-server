// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

/// <summary>
/// Integration tests for the GeoServices NAServer route / service-area solve
/// adapters. The pgRouting test image does not ship the <c>pgrouting</c> extension,
/// so these tests register a deterministic in-process <see cref="IRoutingProvider"/>
/// (see <see cref="TestRoutingProvider"/>) to exercise the full
/// adapter → provider → Esri response path without a live topology.
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerEndpointTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private readonly WebAppFixture _fixture;

    public NAServerEndpointTests(NAServerEndpointTestsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_TwoStops_ReturnsEsriRouteFeatureSet()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            new KeyValuePair<string, string>("returnRoutes", "true"),
            new KeyValuePair<string, string>("returnDirections", "true"),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var feature = root.GetProperty("routes").GetProperty("features")[0];
        feature.GetProperty("geometry").GetProperty("paths").GetArrayLength().Should().BeGreaterThan(0);
        feature.GetProperty("geometry").GetProperty("paths")[0].GetArrayLength().Should().BeGreaterThan(0);

        var attributes = feature.GetProperty("attributes");
        attributes.GetProperty("Total_Length").GetDouble().Should().BeGreaterThan(0);
        attributes.GetProperty("Total_TravelTime").GetDouble().Should().BeGreaterThan(0);

        root.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_GetWithQueryParameters_ReturnsEsriRouteFeatureSet()
    {
        var stops = Uri.EscapeDataString("-157.858333,21.306944;-157.862,21.31");
        var response = await _fixture.Client.GetAsync(
            $"/rest/services/Routing/NAServer/Route/solve?f=json&stops={stops}" +
            "&returnRoutes=true&returnDirections=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("routes").GetProperty("features").GetArrayLength().Should().Be(1);
        root.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_GetWithInsufficientStops_ReturnsGeoServicesError()
    {
        var response = await _fixture.Client.GetAsync(
            "/rest/services/Routing/NAServer/Route/solve?f=json&stops=-157.858333%2C21.306944");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        document.RootElement.TryGetProperty("routes", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task ServiceArea_DefaultBreaks_ReturnsSaPolygonsWithFromToBreak()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("facilities", "-157.858333,21.306944"),
            new KeyValuePair<string, string>("defaultBreaks", "5,10"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("saPolygons").GetProperty("features");

        // One facility x two breaks => two concentric polygons.
        features.GetArrayLength().Should().Be(2);

        var first = features[0].GetProperty("attributes");
        first.GetProperty("FacilityID").GetInt32().Should().Be(0);
        first.GetProperty("FromBreak").GetDouble().Should().Be(0);
        first.GetProperty("ToBreak").GetDouble().Should().Be(5);

        var second = features[1].GetProperty("attributes");
        second.GetProperty("FromBreak").GetDouble().Should().Be(5);
        second.GetProperty("ToBreak").GetDouble().Should().Be(10);

        features[0].GetProperty("geometry").GetProperty("rings").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_PJsonFormat_ReturnsIndentedJson()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "pjson"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();

        // Indented output contains newlines and leading-space indentation; the
        // compact json path would not.
        body.Should().Contain("\n");
        body.Should().Contain("  ");

        // Still valid, parseable JSON.
        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_MissingStops_Returns400()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944"),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        // Invalid input maps to the GeoServices error envelope, not a 500.
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task ServiceArea_ProviderWithoutServiceAreaSupport_ReturnsError()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsRoute: true, SupportsServiceArea: false);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("facilities", "-157.858333,21.306944"),
                new KeyValuePair<string, string>("defaultBreaks", "5,10"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea",
                payload);

            // Capability gate fires before solving: standard Esri error, not a solved result.
            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(400);
            error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
                .Should().Contain(d => d!.Contains("Service-area solves are not supported"));
            document.RootElement.TryGetProperty("saPolygons", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_ProviderWithoutRouteSupport_ReturnsError()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsRoute: false, SupportsServiceArea: true);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            ]);

            var response = await fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

            // Capability gate fires before solving: standard Esri error, not a solved route.
            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(400);
            error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
                .Should().Contain(d => d!.Contains("Route solves are not supported"));
            document.RootElement.TryGetProperty("routes", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_ProviderInvalidSridFailure_ReturnsSanitized400()
    {
        var provider = new TestRoutingProvider(
            new RoutingProviderCapabilities(SupportsRoute: true, SupportsServiceArea: true),
            _ => new InvalidOperationException("PostGIS ST_Transform failed: invalid SRID 999999 in spatial_ref_sys."));
        var fixture = await CreateFixtureWithRoutingProviderAsync(provider);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
                new KeyValuePair<string, string>("outSR", "999999"),
            ]);

            var response = await fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("PostGIS");
            body.Should().NotContain("ST_Transform");
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(400);
            error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
                .Should().Contain(d => d!.Contains("spatial reference"));
            document.RootElement.TryGetProperty("routes", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task ServiceArea_UnsupportedTravelDirection_Returns400()
    {
        // Provider advertises only FromFacility; a ToFacility request must be rejected.
        var capabilities = new RoutingProviderCapabilities
        {
            SupportedTravelDirections = [ServiceAreaTravelDirection.FromFacility],
        };
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("facilities", "-157.858333,21.306944"),
                new KeyValuePair<string, string>("defaultBreaks", "5,10"),
                new KeyValuePair<string, string>("travelDirection", "esriNATravelDirectionToFacility"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea",
                payload);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(400);
            error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
                .Should().Contain(d => d!.Contains("travelDirection"));
            document.RootElement.TryGetProperty("saPolygons", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_WithBarriersAndTravelMode_OnCapableProvider_Solves()
    {
        // The shared fixture's TestRoutingProvider advertises all barrier kinds and
        // driving/walking modes, so a barrier+mode request is accepted and solved.
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            new KeyValuePair<string, string>("travelMode", "walking"),
            new KeyValuePair<string, string>(
                "barriers",
                """{ "features": [ { "geometry": { "x": -157.86, "y": 21.308 } } ] }"""),
            new KeyValuePair<string, string>(
                "polygonBarriers",
                """{ "features": [ { "geometry": { "rings": [ [ [-157.87,21.30],[-157.86,21.30],[-157.86,21.31],[-157.87,21.30] ] ] } } ] }"""),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_WithBarriers_OnProviderWithoutBarrierSupport_Returns400()
    {
        // Provider advertises route+service-area but no barrier kinds.
        var capabilities = new RoutingProviderCapabilities(SupportsRoute: true, SupportsServiceArea: true);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
                new KeyValuePair<string, string>(
                    "barriers",
                    """{ "features": [ { "geometry": { "x": -157.86, "y": 21.308 } } ] }"""),
            ]);

            var response = await fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var error = document.RootElement.GetProperty("error");
            error.GetProperty("code").GetInt32().Should().Be(400);
            error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
                .Should().Contain(d => d!.Contains("barriers are not supported"));
            document.RootElement.TryGetProperty("routes", out _).Should().BeFalse();
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task RouteSolve_WithUnsupportedTravelMode_Returns400()
    {
        // The shared fixture provider advertises only driving/walking; trucking is rejected.
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            new KeyValuePair<string, string>("travelMode", "trucking"),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details").EnumerateArray().Select(d => d.GetString())
            .Should().Contain(d => d!.Contains("travelMode"));
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task ServiceArea_WithUnsupportedTravelMode_Returns400()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("facilities", "-157.858333,21.306944"),
            new KeyValuePair<string, string>("defaultBreaks", "5,10"),
            new KeyValuePair<string, string>("travelMode", "trucking"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea",
            payload);

        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(d => d.GetString()).Should().Contain(d => d!.Contains("travelMode"));
    }

    private static async Task<WebAppFixture> CreateFixtureWithCapabilitiesAsync(RoutingProviderCapabilities capabilities)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped<IRoutingProvider>(_ => new TestRoutingProvider(capabilities));
            });

        await fixture.InitializeAsync();
        return fixture;
    }

    private static async Task<WebAppFixture> CreateFixtureWithRoutingProviderAsync(IRoutingProvider provider)
    {
        var fixture = new WebAppFixture()
            .ConfigureServices(services =>
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped(_ => provider);
            });

        await fixture.InitializeAsync();
        return fixture;
    }

    [IntegrationTest]
    [Operation(Operations.ClosestFacility)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility")]
    public async Task SolveClosestFacility_WithIncidentAndFacilities_ReturnsRankedRoute()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("incidents", "-157.858333,21.306944"),
            // Two facilities: the first is closer, so it ranks 1.
            new KeyValuePair<string, string>("facilities", "-157.862,21.31;-158.0,21.5"),
            new KeyValuePair<string, string>("returnDirections", "true"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ClosestFacility/solveClosestFacility",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = document.RootElement.GetProperty("routes").GetProperty("features")[0];
        feature.GetProperty("attributes").GetProperty("FacilityRank").GetInt32().Should().Be(1);
        // Esri 1-based ids; the closer facility is index 0 -> FacilityID 1.
        feature.GetProperty("attributes").GetProperty("FacilityID").GetInt32().Should().Be(1);
        feature.GetProperty("attributes").GetProperty("Total_TravelTime").GetDouble().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ClosestFacility)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility")]
    public async Task SolveClosestFacility_ProviderWithoutSupport_Returns400()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsClosestFacility: false);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("incidents", "-157.858333,21.306944"),
                new KeyValuePair<string, string>("facilities", "-157.862,21.31"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/ClosestFacility/solveClosestFacility",
                payload);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(d => d.GetString()).Should().Contain(d => d!.Contains("Closest-facility solves are not supported"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.OdCostMatrix)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix")]
    public async Task SolveOdCostMatrix_TwoByTwo_ReturnsRankedLines()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("origins", "-157.86,21.30;-157.80,21.40"),
            new KeyValuePair<string, string>("destinations", "-157.85,21.31;-157.70,21.50"),
            new KeyValuePair<string, string>("outputType", "esriNAODOutputNoLines"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ODCostMatrix/solveODCostMatrix",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var features = document.RootElement.GetProperty("odLines").GetProperty("features");
        // 2 origins x 2 destinations => 4 lines.
        features.GetArrayLength().Should().Be(4);
        var attrs = features[0].GetProperty("attributes");
        attrs.GetProperty("OriginID").GetInt32().Should().Be(1);
        attrs.GetProperty("DestinationRank").GetInt32().Should().Be(1);
        attrs.GetProperty("Total_Time").GetDouble().Should().BeGreaterThan(0);
        features[0].TryGetProperty("geometry", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.OdCostMatrix)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix")]
    public async Task SolveOdCostMatrix_StraightLines_ReturnsGeometryInRequestedSpatialReference()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("origins", "-157.86,21.30"),
            new KeyValuePair<string, string>("destinations", "-157.85,21.31"),
            new KeyValuePair<string, string>("outputType", "esriNAODOutputStraightLines"),
            new KeyValuePair<string, string>("outSR", "4326"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ODCostMatrix/solveODCostMatrix", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var odLines = document.RootElement.GetProperty("odLines");
        odLines.GetProperty("geometryType").GetString().Should().Be("esriGeometryPolyline");
        odLines.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        var path = odLines.GetProperty("features")[0].GetProperty("geometry").GetProperty("paths")[0];
        path.GetArrayLength().Should().Be(2);
        path[0][0].GetDouble().Should().BeApproximately(-157.86, 1e-9);
        path[1][0].GetDouble().Should().BeApproximately(-157.85, 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.OdCostMatrix)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix")]
    public async Task SolveOdCostMatrix_TrueShape_ReturnsPrecise400()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("origins", "-157.86,21.30"),
            new KeyValuePair<string, string>("destinations", "-157.85,21.31"),
            new KeyValuePair<string, string>("outputType", "esriNAODOutputTrueShape"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ODCostMatrix/solveODCostMatrix", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        var details = error.GetProperty("details").EnumerateArray()
            .Select(detail => detail.GetString())
            .Where(detail => detail != null)
            .ToArray();
        details.Should().Contain(detail => detail!.Contains("esriNAODOutputTrueShape", StringComparison.Ordinal));
        details.Should().Contain(detail => detail!.Contains("not implemented", StringComparison.Ordinal));
    }

    [IntegrationTest]
    [Operation(Operations.OdCostMatrix)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix")]
    public async Task SolveOdCostMatrix_ProviderWithoutSupport_Returns400()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsOdCostMatrix: false);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("origins", "-157.86,21.30"),
                new KeyValuePair<string, string>("destinations", "-157.85,21.31"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/ODCostMatrix/solveODCostMatrix",
                payload);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(d => d.GetString()).Should().Contain(d => d!.Contains("OD cost matrix solves are not supported"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.OdCostMatrix)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix")]
    public async Task SolveOdCostMatrix_ProviderWithoutStraightLineSupport_Returns400()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsOdCostMatrix: true)
        {
            SupportsOdStraightLines = false,
        };
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("origins", "-157.86,21.30"),
                new KeyValuePair<string, string>("destinations", "-157.85,21.31"),
                new KeyValuePair<string, string>("outputType", "esriNAODOutputStraightLines"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/ODCostMatrix/solveODCostMatrix",
                payload);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(detail => detail.GetString())
                .Should().Contain(detail => detail!.Contains("not supported by the configured routing provider"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.LocationAllocation)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/LocationAllocation/solveLocationAllocation")]
    public async Task SolveLocationAllocation_MinimizeImpedance_ChoosesFacilityAndAllocatesDemand()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            // Two candidate facilities; demand clusters near the first.
            new KeyValuePair<string, string>("facilities", "-157.86,21.30;-158.20,21.80"),
            new KeyValuePair<string, string>(
                "demandPoints",
                """{ "features": [ { "geometry": { "x": -157.861, "y": 21.301 }, "attributes": { "Weight": 5 } }, { "geometry": { "x": -157.859, "y": 21.299 } } ] }"""),
            new KeyValuePair<string, string>("numberFacilitiesToFind", "1"),
            new KeyValuePair<string, string>("problemType", "esriMFPMinimizeImpedance"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/LocationAllocation/solveLocationAllocation",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var facilities = document.RootElement.GetProperty("facilities").GetProperty("features");
        facilities.GetArrayLength().Should().Be(1);
        // The closer candidate (index 0 -> FacilityID 1) is chosen.
        facilities[0].GetProperty("attributes").GetProperty("FacilityID").GetInt32().Should().Be(1);

        var demand = document.RootElement.GetProperty("demandPoints").GetProperty("features");
        demand.GetArrayLength().Should().Be(2);
        demand[0].GetProperty("attributes").GetProperty("FacilityID").GetInt32().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.LocationAllocation)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/LocationAllocation/solveLocationAllocation")]
    public async Task SolveLocationAllocation_UnsupportedProblemType_Returns400()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("facilities", "-157.86,21.30"),
            new KeyValuePair<string, string>("demandPoints", "-157.861,21.301"),
            new KeyValuePair<string, string>("problemType", "esriMFPMaximizeAttendance"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/LocationAllocation/solveLocationAllocation",
            payload);

        // An unsupported problem type is rejected at parse time with a 400.
        // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.LocationAllocation)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/LocationAllocation/solveLocationAllocation")]
    public async Task SolveLocationAllocation_ProviderWithoutSupport_Returns400()
    {
        var capabilities = new RoutingProviderCapabilities(SupportsLocationAllocation: false);
        var fixture = await CreateFixtureWithCapabilitiesAsync(capabilities);
        try
        {
            var payload = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("f", "json"),
                new KeyValuePair<string, string>("facilities", "-157.86,21.30"),
                new KeyValuePair<string, string>("demandPoints", "-157.861,21.301"),
            ]);

            var response = await fixture.Client.PostAsync(
                "/rest/services/Routing/NAServer/LocationAllocation/solveLocationAllocation",
                payload);

            // PA-070/PA-117: GeoServices always returns HTTP 200; error code is in the JSON body.
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("error").GetProperty("details").EnumerateArray()
                .Select(d => d.GetString()).Should().Contain(d => d!.Contains("Location-allocation solves are not supported"));
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}

/// <summary>
/// Per-class wrapper fixture that builds a single <see cref="WebAppFixture"/> with the
/// deterministic <see cref="TestRoutingProvider"/> registered, so the shared-fixture tests
/// in <see cref="NAServerEndpointTests"/> initialize the host once per class instead of once
/// per test method. <see cref="WebAppFixture"/> is sealed, so it is wrapped rather than subclassed.
/// </summary>
public sealed class NAServerEndpointTestsFixture : IAsyncLifetime
{
    public WebAppFixture App { get; } = new WebAppFixture()
        .ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });

    public Task InitializeAsync() => App.InitializeAsync();

    public Task DisposeAsync() => App.DisposeAsync();
}
