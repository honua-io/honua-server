// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Integration tests for the OGC SensorThings API (STA v1.1) Phase 2 ingest endpoints
/// (#1747): REST/bulk observation creation and datastream creation. Ingested observations
/// must be visible through the Phase 1 read API, proving the ingest → store → read loop.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsIngestEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    public async Task PostObservation_SingleIntoSeededDatastream_PersistsAndIsReadable()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var payload = Json("""
            { "phenomenonTime": "2026-06-20T12:00:00Z", "result": 42.5, "Datastream": { "@iot.id": 1 } }
            """);

        var response = await adminClient.PostAsync("/sta/v1.1/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("@iot.id").GetInt64();
        id.Should().BeGreaterThan(0);
        doc.RootElement.GetProperty("result").GetDouble().Should().Be(42.5);

        // The new observation must be readable through the Phase 1 read API.
        var read = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({id})");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    public async Task PostObservations_BulkEnvelope_PersistsAllRows()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var payload = Json("""
            { "value": [
                { "phenomenonTime": "2026-06-20T13:00:00Z", "result": 1.0, "Datastream": { "@iot.id": 1 } },
                { "phenomenonTime": "2026-06-20T14:00:00Z", "result": 2.0, "Datastream": { "@iot.id": 1 } },
                { "phenomenonTime": "2026-06-20T15:00:00Z", "result": 3.0, "Datastream": { "@iot.id": 1 } }
            ] }
            """);

        var response = await adminClient.PostAsync("/sta/v1.1/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(3);
        doc.RootElement.GetProperty("value").GetArrayLength().Should().Be(3);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams({id})/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    public async Task PostObservation_OnDatastreamNavigation_PersistsToThatDatastream()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var payload = Json("""{ "result": 7.0 }""");

        var response = await adminClient.PostAsync("/sta/v1.1/Datastreams(1)/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("result").GetDouble().Should().Be(7.0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    public async Task PostObservation_MissingDatastream_Returns404()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var payload = Json("""{ "result": 1.0, "Datastream": { "@iot.id": 999999 } }""");

        var response = await adminClient.PostAsync("/sta/v1.1/Observations", payload);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [InterfaceOperation(TestProtocols.SensorThings, "datastream.create")]
    public async Task PostDatastream_WithInlineRelatedEntities_CreatesQueryableDatastream()
    {
        using var adminClient = _fixture.CreateAdminClient();
        var payload = Json("""
            {
              "name": "Test Wind Speed",
              "description": "Created by the ingest integration test",
              "unitOfMeasurement": { "name": "metre per second", "symbol": "m/s", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
              "Thing": { "name": "Test Station", "description": "test" },
              "Sensor": { "name": "Anemometer", "description": "test" },
              "ObservedProperty": { "name": "Wind Speed", "description": "test" }
            }
            """);

        var response = await adminClient.PostAsync("/sta/v1.1/Datastreams", payload);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = doc.RootElement.GetProperty("@iot.id").GetInt64();
        id.Should().BeGreaterThan(0);

        // Created datastream must be readable, and ingest into it must round-trip.
        var read = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({id})");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        var observation = Json("""{ "result": 5.5 }""");
        var ingest = await adminClient.PostAsync($"/sta/v1.1/Datastreams({id})/Observations", observation);
        ingest.StatusCode.Should().Be(HttpStatusCode.Created);

        var observations = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({id})/Observations");
        using var obsDoc = JsonDocument.Parse(await observations.Content.ReadAsStringAsync());
        obsDoc.RootElement.GetProperty("value").GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    public async Task PostDatastream_MissingBody_Returns400()
    {
        using var adminClient = _fixture.CreateAdminClient();

        var response = await adminClient.PostAsync("/sta/v1.1/Datastreams", Json("{}"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
