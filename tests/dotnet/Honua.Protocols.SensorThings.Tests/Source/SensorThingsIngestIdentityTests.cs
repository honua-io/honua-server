// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>
/// Identifier-allocation regressions for SensorThings ingest (#4199). Observation ids used
/// to be reserved with <c>SELECT COALESCE(MAX(id), 0) + 1</c> inside a READ COMMITTED
/// transaction and no lock, so two overlapping writers read the same MAX. Because the
/// observation primary key is <c>(id, phenomenon_time)</c> the duplicate insert succeeded
/// whenever the phenomenon times differed: two observations silently shared one
/// <c>@iot.id</c> and the <c>Location</c> of one 201 resolved to the other row.
///
/// Every expectation here is computed from the request the test issued, never read back
/// from the response it is checking: the posted result value is the oracle for "this
/// <c>@iot.id</c> belongs to my observation".
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsIngestIdentityTests : IAsyncLifetime
{
    /// <summary>Seeded observations in the fixture datastream (ids 1..48, migration 059).</summary>
    private const int SeededObservationCount = 48;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static string Instant(int index) =>
        new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)
            .AddMinutes(index)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The result value posted by writer <paramref name="index"/>, computed, not observed.</summary>
    private static double ExpectedResult(int index) => 1000d + (index * 0.5d);

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    public async Task PostObservations_FromConcurrentWriters_AllocateDistinctIdsThatResolveToTheirOwnResult()
    {
        const int writers = 32;
        using var adminClient = _fixture.CreateAdminClient();

        // Distinct phenomenonTime per writer is the case the old allocator lost silently:
        // the duplicate (id, phenomenon_time) row was accepted instead of rejected.
        var posts = Enumerable.Range(0, writers).Select(async index =>
        {
            var body = Json($$"""
                {
                  "phenomenonTime": "{{Instant(index)}}",
                  "result": {{ExpectedResult(index).ToString(CultureInfo.InvariantCulture)}},
                  "Datastream": { "@iot.id": 1 }
                }
                """);
            using var response = await adminClient.PostAsync("/sta/v1.1/Observations", body);
            response.StatusCode.Should().Be(HttpStatusCode.Created,
                "every concurrent ingest must succeed, not collide on a reserved id");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (Index: index, Id: document.RootElement.GetProperty("@iot.id").GetInt64());
        }).ToArray();

        var allocations = await Task.WhenAll(posts);

        allocations.Select(allocation => allocation.Id).Should().OnlyHaveUniqueItems(
            "concurrent writers must never be handed the same @iot.id");

        // Identity: each returned id must resolve to the observation that writer posted.
        foreach (var (index, id) in allocations)
        {
            using var read = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({id})");
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("result").GetDouble().Should().Be(
                ExpectedResult(index),
                "Observations({0}) must return the reading writer {1} posted", id, index);
            document.RootElement.GetProperty("phenomenonTime").GetString().Should()
                .StartWith(Instant(index)[..16]);
        }

        // And the store must hold exactly the rows the test created, with no duplicate ids
        // hidden behind the composite primary key.
        using var all = await _fixture.Client.GetAsync("/sta/v1.1/Observations?$top=1000&$count=true");
        using var allDocument = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        allDocument.RootElement.GetProperty("@iot.count").GetInt64().Should()
            .Be(SeededObservationCount + writers);
        allDocument.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("@iot.id").GetInt64())
            .Should().OnlyHaveUniqueItems();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    [InterfaceOperation(TestProtocols.SensorThings, "sensor.ingest")]
    public async Task PostObservations_ConcurrentBulkEnvelopes_AllocateDistinctIdsAcrossBatches()
    {
        const int batches = 8;
        const int rowsPerBatch = 5;
        using var adminClient = _fixture.CreateAdminClient();

        var posts = Enumerable.Range(0, batches).Select(async batch =>
        {
            var rows = string.Join(",", Enumerable.Range(0, rowsPerBatch).Select(row =>
            {
                var index = (batch * rowsPerBatch) + row;
                return $$"""
                    { "phenomenonTime": "{{Instant(index)}}", "result": {{ExpectedResult(index).ToString(CultureInfo.InvariantCulture)}}, "Datastream": { "@iot.id": 1 } }
                    """;
            }));
            using var response = await adminClient.PostAsync("/sta/v1.1/Observations", Json($$"""{ "value": [{{rows}}] }"""));
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(rowsPerBatch);
            return document.RootElement.GetProperty("value").EnumerateArray()
                .Select(value => value.GetInt64()).ToArray();
        }).ToArray();

        var allocated = (await Task.WhenAll(posts)).SelectMany(ids => ids).ToArray();

        allocated.Should().HaveCount(batches * rowsPerBatch);
        allocated.Should().OnlyHaveUniqueItems("bulk batches must not overlap each other's id block");

        using var all = await _fixture.Client.GetAsync("/sta/v1.1/Observations?$top=0&$count=true");
        using var allDocument = JsonDocument.Parse(await all.Content.ReadAsStringAsync());
        allDocument.RootElement.GetProperty("@iot.count").GetInt64().Should()
            .Be(SeededObservationCount + (batches * rowsPerBatch));
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    public async Task PostObservation_AfterSeededRows_ContinuesTheIdSeriesInsteadOfReusingIt()
    {
        using var adminClient = _fixture.CreateAdminClient();

        using var response = await adminClient.PostAsync(
            "/sta/v1.1/Observations",
            Json($$"""{ "phenomenonTime": "{{Instant(0)}}", "result": 12.25, "Datastream": { "@iot.id": 1 } }"""));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        // The fixture seeds observation ids 1..48 explicitly, so the sequence must be
        // positioned at 48 and hand out 49 — not restart at 1 and collide.
        document.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(SeededObservationCount + 1);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [InterfaceOperation(TestProtocols.SensorThings, "datastream.create")]
    public async Task PostDatastreams_FromConcurrentWriters_AllocateDistinctIdsForDatastreamsAndRelatedEntities()
    {
        const int writers = 12;
        using var adminClient = _fixture.CreateAdminClient();

        var posts = Enumerable.Range(0, writers).Select(async index =>
        {
            var body = Json($$"""
                {
                  "name": "Concurrent Datastream {{index}}",
                  "description": "writer {{index}}",
                  "unitOfMeasurement": { "name": "metre per second", "symbol": "m/s", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
                  "Thing": { "name": "Station {{index}}", "description": "writer {{index}}" },
                  "Sensor": { "name": "Anemometer {{index}}", "description": "writer {{index}}" },
                  "ObservedProperty": { "name": "Wind Speed {{index}}", "description": "writer {{index}}" }
                }
                """);
            using var response = await adminClient.PostAsync("/sta/v1.1/Datastreams", body);
            response.StatusCode.Should().Be(HttpStatusCode.Created,
                "a single-column catalog primary key turned the id race into a 23505 and a 500");
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return (Index: index, Id: document.RootElement.GetProperty("@iot.id").GetInt64());
        }).ToArray();

        var allocations = await Task.WhenAll(posts);
        allocations.Select(allocation => allocation.Id).Should().OnlyHaveUniqueItems();

        foreach (var (index, id) in allocations)
        {
            using var read = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({id})");
            read.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            document.RootElement.GetProperty("name").GetString().Should().Be($"Concurrent Datastream {index}");
        }

        // Each writer also created a Thing, a Sensor, and an ObservedProperty inline; those
        // allocations must be distinct too (one seeded row of each is in the fixture).
        foreach (var entitySet in new[] { "Things", "Sensors", "ObservedProperties" })
        {
            using var read = await _fixture.Client.GetAsync($"/sta/v1.1/{entitySet}?$top=1000");
            using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
            var ids = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(value => value.GetProperty("@iot.id").GetInt64()).ToArray();
            ids.Should().OnlyHaveUniqueItems("{0} ids must be unique", entitySet);
            ids.Should().HaveCount(writers + 1, "each writer created one {0} entity", entitySet);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    public async Task PostDatastream_AfterAClientSuppliedId_AllocatesAboveItRatherThanColliding()
    {
        using var adminClient = _fixture.CreateAdminClient();

        // A deep insert may carry an explicit @iot.id, which bypasses the column default.
        using var explicitResponse = await adminClient.PostAsync("/sta/v1.1/Datastreams", Json("""
            {
              "name": "Explicit Thing Datastream",
              "description": "carries a client-chosen Thing id",
              "unitOfMeasurement": { "name": "degree Celsius", "symbol": "C", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
              "Thing": { "@iot.id": 500, "name": "Client Numbered Station", "description": "explicit id" },
              "Sensor": { "name": "Thermometer A", "description": "test" },
              "ObservedProperty": { "name": "Air Temperature A", "description": "test" }
            }
            """));
        explicitResponse.StatusCode.Should().Be(HttpStatusCode.Created);

        using var explicitThing = await _fixture.Client.GetAsync("/sta/v1.1/Things(500)");
        explicitThing.StatusCode.Should().Be(HttpStatusCode.OK, "the client-supplied Thing id must be honoured");

        // The next server-allocated Thing must land above the client-chosen id.
        using var followUp = await adminClient.PostAsync("/sta/v1.1/Datastreams", Json("""
            {
              "name": "Server Numbered Datastream",
              "description": "server-allocated Thing id",
              "unitOfMeasurement": { "name": "degree Celsius", "symbol": "C", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
              "Thing": { "name": "Server Numbered Station", "description": "generated id" },
              "Sensor": { "name": "Thermometer B", "description": "test" },
              "ObservedProperty": { "name": "Air Temperature B", "description": "test" }
            }
            """));
        followUp.StatusCode.Should().Be(HttpStatusCode.Created);

        using var things = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=1000");
        using var document = JsonDocument.Parse(await things.Content.ReadAsStringAsync());
        var created = document.RootElement.GetProperty("value").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "Server Numbered Station");
        created.GetProperty("@iot.id").GetInt64().Should().Be(501,
            "the sequence must be advanced past a client-supplied id so the next allocation cannot collide");
    }
}
