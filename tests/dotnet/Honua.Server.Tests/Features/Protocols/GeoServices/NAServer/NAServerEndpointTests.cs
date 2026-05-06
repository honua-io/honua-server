// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;

[Collection("Database")]
[Protocol(TestProtocols.NAServer)]
public sealed class NAServerEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Directions)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task SolveRoute_WithDirectionsPayload_ReturnsRoutesAndDirections()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31"),
            new KeyValuePair<string, string>("returnDirections", "true")
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("directions").GetArrayLength().Should().BeGreaterThan(0);
        document.RootElement.GetProperty("routes").GetProperty("features")[0]
            .GetProperty("attributes").GetProperty("Name").GetString().Should().Be("Route 1");
    }

    [IntegrationTest]
    [Operation(Operations.OptimizeRoute)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/Route/solve")]
    public async Task SolveRoute_WithFindBestSequence_ReturnsRoutes()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("stops", "-157.858333,21.306944;-157.862,21.31;-157.85,21.32"),
            new KeyValuePair<string, string>("findBestSequence", "true")
        ]);

        var response = await _fixture.Client.PostAsync("/rest/services/Routing/NAServer/Route/solve", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("routes").GetProperty("features").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.ServiceArea)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea")]
    public async Task SolveServiceArea_WithFacilityPayload_ReturnsEmptyObject()
    {
        var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("facilities", "-157.858333,21.306944"),
            new KeyValuePair<string, string>("defaultBreaks", "5")
        ]);

        var response = await _fixture.Client.PostAsync(
            "/rest/services/Routing/NAServer/ServiceArea/solveServiceArea",
            payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        document.RootElement.EnumerateObject().Should().BeEmpty();
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
            new KeyValuePair<string, string>("facilities", "-157.862,21.31")
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
