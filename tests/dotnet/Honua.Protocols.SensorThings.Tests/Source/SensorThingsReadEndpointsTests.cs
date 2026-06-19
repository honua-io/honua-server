// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Integration tests for the OGC SensorThings API (STA v1.1) read endpoints.
/// The synthetic datastream (id=1) seeded by migration 059 makes the read path
/// demoable end-to-end: list/by-id, navigation, and $filter/$orderby/$expand.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsReadEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_ReturnsConformanceShapedCollection()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Things");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("value", out var value).Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    public async Task Things_ById_ReturnsEntityEnvelope()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Things(1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(1);
        doc.RootElement.GetProperty("@iot.selfLink").GetString().Should().Contain("/sta/v1.1/Things(1)");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Sensors")]
    public async Task Sensors_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Sensors");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/Sensors({id})")]
    public async Task Sensors_ById_ReturnsEntity()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Sensors(1)");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/ObservedProperties")]
    public async Task ObservedProperties_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/ObservedProperties");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/ObservedProperties({id})")]
    public async Task ObservedProperties_ById_ReturnsEntity()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/ObservedProperties(1)");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task Datastreams_ReturnsCollectionWithNavigationLinks()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Datastreams");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var first = doc.RootElement.GetProperty("value").EnumerateArray().First();
        first.GetProperty("Observations@iot.navigationLink").GetString().Should().Contain("/Observations");
        first.GetProperty("unitOfMeasurement").GetProperty("symbol").GetString().Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastreams_ById_ReturnsEntity()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Datastreams(1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastreams_WithExpandObservations_InlinesObservations()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Datastreams(1)?$expand=Observations");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.TryGetProperty("Observations", out var observations).Should().BeTrue();
        observations.ValueKind.Should().Be(JsonValueKind.Array);
        observations.GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Observations")]
    public async Task DatastreamObservations_OrderByDescending_ReturnsObservations()
    {
        var response = await _fixture.Client.GetAsync(
            "/sta/v1.1/Datastreams(1)/Observations?$orderby=phenomenonTime desc&$top=5");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var observations = doc.RootElement.GetProperty("value");
        observations.GetArrayLength().Should().BeGreaterThan(0);

        var times = observations.EnumerateArray()
            .Select(o => o.GetProperty("phenomenonTime").GetString())
            .ToList();
        times.Should().BeInDescendingOrder();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_ReturnsCollection()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Observations?$top=10");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_WithResultFilter_FiltersByResult()
    {
        var filter = Uri.EscapeDataString("result gt 20");
        var response = await _fixture.Client.GetAsync($"/sta/v1.1/Observations?$filter={filter}&$top=100");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        foreach (var observation in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            observation.GetProperty("result").GetDouble().Should().BeGreaterThan(20);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_InvalidFilter_Returns400()
    {
        var filter = Uri.EscapeDataString("nonexistent gt 1");
        var response = await _fixture.Client.GetAsync($"/sta/v1.1/Observations?$filter={filter}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task Observations_ById_ReturnsEntity()
    {
        var response = await _fixture.Client.GetAsync("/sta/v1.1/Observations(1)");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("Datastream@iot.navigationLink").GetString().Should().Contain("/Datastream");
    }
}
