// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Routing.Features.Routing.Abstractions;
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
[Collection("Database")]
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

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
    public async Task RouteSolve_MissingStops_Returns400()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944"),
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        // Invalid input maps to the GeoServices error envelope, not a 500.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.ClosestFacility)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility")]
    public async Task SolveClosestFacility_WithIncidentAndFacilityPayload_ReturnsDirections()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("incidents", "-157.858333,21.306944"),
            new KeyValuePair<string, string>("facilities", "-157.862,21.31"),
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ClosestFacility/solveClosestFacility",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("directions")[0]
            .GetProperty("summary").GetProperty("routeName").GetString().Should().Be("Incident - Facility A");
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }
}
