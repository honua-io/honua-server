// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsCoreCrudTests : IAsyncLifetime
{
    private static readonly string[] RequiredTables = ["sta_thing", "sta_location", "sta_historical_location", "sta_datastream", "sta_sensor", "sta_observed_property", "sta_observation", "sta_feature_of_interest"];
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });
    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        await using var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM information_schema.tables WHERE table_schema = @schema AND table_name = ANY(@tables);";
        command.Parameters.AddWithValue("schema", _fixture.CurrentSchema!);
        command.Parameters.AddWithValue("tables", RequiredTables);
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(8, "the production migration must provision all sensing entities before endpoint replay");
    }
    public Task DisposeAsync() => _fixture.DisposeAsync();
    private static StringContent Body(JsonNode body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");
    private static JsonObject Reference(long id) => new() { ["@iot.id"] = id };
    private async Task<JsonObject> CreateAsync(string set, JsonObject body)
    {
        using var admin = _fixture.CreateAdminClient();
        using var response = await admin.PostAsync("/sta/v1.1/" + set, Body(body));
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
    private static long Id(JsonObject entity) => entity["@iot.id"]!.GetValue<long>();
    private static DateTimeOffset ParseInstant(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private async Task<JsonObject> ReadAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync("/sta/v1.1/" + path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }
    private static JsonObject Location(string name) => new()
    {
        ["name"] = name, ["description"] = "Synthetic test location", ["encodingType"] = "application/geo+json",
        ["location"] = JsonNode.Parse("""{"type":"Point","coordinates":[-157.8,21.3]}""")
    };
    private static JsonObject Datastream(long thing) => new()
    {
        ["name"] = "Measurement", ["description"] = "Synthetic measurement",
        ["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Measurement",
        ["unitOfMeasurement"] = new JsonObject { ["name"] = "Celsius", ["symbol"] = "C", ["definition"] = "https://example.test/celsius" },
        ["Thing"] = Reference(thing),
        ["Sensor"] = new JsonObject { ["name"] = "Sensor", ["description"] = "Test sensor", ["encodingType"] = "text/plain", ["metadata"] = "Synthetic sensor" },
        ["ObservedProperty"] = new JsonObject { ["name"] = "Temperature", ["description"] = "Test temperature", ["definition"] = "https://example.test/temperature" }
    };

    [IntegrationTheory]
    [InlineData("Things")]
    [InlineData("Locations")]
    [InlineData("HistoricalLocations")]
    [InlineData("Datastreams")]
    [InlineData("Sensors")]
    [InlineData("ObservedProperties")]
    [InlineData("Observations")]
    [InlineData("FeaturesOfInterest")]
    [Operation(Operations.Update)]
    [Endpoint("POST /sta/v1.1/HistoricalLocations")]
    [Endpoint("POST /sta/v1.1/Sensors")]
    [Endpoint("POST /sta/v1.1/ObservedProperties")]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("PATCH /sta/v1.1/Locations({id})")]
    [Endpoint("PATCH /sta/v1.1/HistoricalLocations({id})")]
    [Endpoint("PATCH /sta/v1.1/Datastreams({id})")]
    [Endpoint("PATCH /sta/v1.1/Sensors({id})")]
    [Endpoint("PATCH /sta/v1.1/ObservedProperties({id})")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    [Endpoint("PATCH /sta/v1.1/FeaturesOfInterest({id})")]
    [Endpoint("DELETE /sta/v1.1/Things({id})")]
    [Endpoint("DELETE /sta/v1.1/Locations({id})")]
    [Endpoint("DELETE /sta/v1.1/HistoricalLocations({id})")]
    [Endpoint("DELETE /sta/v1.1/Datastreams({id})")]
    [Endpoint("DELETE /sta/v1.1/Sensors({id})")]
    [Endpoint("DELETE /sta/v1.1/ObservedProperties({id})")]
    [Endpoint("DELETE /sta/v1.1/Observations({id})")]
    [Endpoint("DELETE /sta/v1.1/FeaturesOfInterest({id})")]
    public async Task EveryEntity_SupportsCreatePatchReadAndDelete(string set)
    {
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var historicalLocation = set == "HistoricalLocations" ? Id(await CreateAsync("Locations", Location("Historical CRUD location"))) : 0;
        var body = set switch
        {
            "Locations" => Location("CRUD location"),
            "FeaturesOfInterest" => new JsonObject { ["name"] = "CRUD feature", ["description"] = "Synthetic", ["encodingType"] = "application/geo+json", ["feature"] = JsonNode.Parse("""{"type":"Point","coordinates":[1,2]}""") },
            "Sensors" => new JsonObject { ["name"] = "CRUD sensor", ["description"] = "Synthetic", ["encodingType"] = "application/pdf", ["metadata"] = "https://example.test/sensor.pdf" },
            "ObservedProperties" => new JsonObject { ["name"] = "CRUD property", ["description"] = "Synthetic", ["definition"] = "https://example.test/property" },
            "Datastreams" => Datastream(1),
            "Observations" => new JsonObject { ["result"] = 1.25, ["Datastream"] = Reference(1), ["FeatureOfInterest"] = Reference(feature) },
            "HistoricalLocations" => new JsonObject { ["time"] = "2026-01-01T00:00:00Z", ["Thing"] = Reference(1), ["Locations"] = new JsonArray(Reference(historicalLocation)) },
            _ => new JsonObject { ["name"] = "CRUD thing", ["description"] = "Synthetic" }
        };
        var entity = await CreateAsync(set, body);
        var property = set == "Observations" ? "result" : set == "HistoricalLocations" ? "time" : "description";
        JsonNode value = set == "Observations" ? JsonValue.Create(9.5)! : JsonValue.Create(set == "HistoricalLocations" ? "2026-02-01T00:00:00Z" : "Updated description")!;
        using var admin = _fixture.CreateAdminClient();
        using var patch = await admin.PatchAsync($"/sta/v1.1/{set}({Id(entity)})", Body(new JsonObject { [property] = value.DeepClone() }));
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var updated = await ReadAsync($"{set}({Id(entity)})");
        if (set == "HistoricalLocations") DateTimeOffset.Parse(updated[property]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture).Should().Be(DateTimeOffset.Parse(value.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture));
        else JsonNode.DeepEquals(updated[property], value).Should().BeTrue();
        using var deleted = await admin.DeleteAsync($"/sta/v1.1/{set}({Id(entity)})");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missing = await _fixture.Client.GetAsync($"/sta/v1.1/{set}({Id(entity)})");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Locations")]
    [Endpoint("POST /sta/v1.1/Things")]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Locations")]
    [Endpoint("GET /sta/v1.1/Locations({id})")]
    [Endpoint("GET /sta/v1.1/HistoricalLocations")]
    [Endpoint("GET /sta/v1.1/HistoricalLocations({id})")]
    [Endpoint("GET /sta/v1.1/FeaturesOfInterest")]
    [Endpoint("GET /sta/v1.1/FeaturesOfInterest({id})")]
    [Endpoint("GET /sta/v1.1/Things({id})/Locations")]
    [Endpoint("GET /sta/v1.1/Things({id})/HistoricalLocations")]
    [Endpoint("GET /sta/v1.1/Locations({id})/Things")]
    [Endpoint("GET /sta/v1.1/Locations({id})/HistoricalLocations")]
    [Endpoint("GET /sta/v1.1/HistoricalLocations({id})/Thing")]
    [Endpoint("GET /sta/v1.1/HistoricalLocations({id})/Locations")]
    [Endpoint("GET /sta/v1.1/Observations({id})/FeatureOfInterest")]
    [Endpoint("GET /sta/v1.1/FeaturesOfInterest({id})/Observations")]
    public async Task DeepInsert_AllEightEntitiesHaveReadableCollectionsSingletonsAndLinks()
    {
        var location = await CreateAsync("Locations", Location("Sampling point"));
        await CreateAsync("Things", new JsonObject { ["name"] = "Unrelated padding thing", ["description"] = "Keeps relationship identifiers distinct" });
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Device", ["description"] = "Synthetic device", ["Locations"] = new JsonArray(Reference(Id(location))) });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var observation = await CreateAsync("Observations", new JsonObject { ["result"] = 12.5, ["Datastream"] = Reference(Id(stream)) });
        observation.ContainsKey("resultTime").Should().BeTrue();
        observation["resultTime"].Should().BeNull();
        foreach (var set in new[] { "Things", "Locations", "HistoricalLocations", "Datastreams", "Sensors", "ObservedProperties", "Observations", "FeaturesOfInterest" })
        {
            var collection = await ReadAsync(set + "?$top=1");
            collection["value"]!.AsArray().Should().NotBeEmpty();
            var entity = collection["value"]![0]!.AsObject();
            var singleton = await ReadAsync($"{set}({Id(entity)})");
            singleton["@iot.selfLink"]!.GetValue<string>().Should().Contain($"{set}({Id(entity)})");
        }
        using var nullProperty = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({Id(observation)})/resultTime/$value");
        nullProperty.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var related = await ReadAsync($"Observations({Id(observation)})/FeatureOfInterest");
        related["feature"]!["type"]!.GetValue<string>().Should().Be("Point");
        var streamThing = await ReadAsync($"Datastreams({Id(stream)})/Thing");
        Id(streamThing).Should().Be(Id(thing));
        Id(streamThing).Should().NotBe(Id(stream));
        var sensor = await ReadAsync($"Datastreams({Id(stream)})/Sensor");
        sensor["metadata"]!.GetValue<string>().Should().Be("Synthetic sensor");
        var property = await ReadAsync($"Datastreams({Id(stream)})/ObservedProperty");
        property["definition"]!.GetValue<string>().Should().Be("https://example.test/temperature");
        var thingLocations = await ReadAsync($"Things({Id(thing)})/Locations");
        Id(thingLocations["value"]![0]!.AsObject()).Should().Be(Id(location));
        var locationThings = await ReadAsync($"Locations({Id(location)})/Things");
        Id(locationThings["value"]![0]!.AsObject()).Should().Be(Id(thing));
        var histories = await ReadAsync($"Things({Id(thing)})/HistoricalLocations");
        var historyId = Id(histories["value"]![0]!.AsObject());
        Id(await ReadAsync($"HistoricalLocations({historyId})/Thing")).Should().Be(Id(thing));
        var historicalLocations = await ReadAsync($"HistoricalLocations({historyId})/Locations");
        Id(historicalLocations["value"]![0]!.AsObject()).Should().Be(Id(location));
        var inverseHistory = await ReadAsync($"Locations({Id(location)})/HistoricalLocations");
        inverseHistory["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(historyId);
        var featureObservations = await ReadAsync($"FeaturesOfInterest({Id(related)})/Observations");
        Id(featureObservations["value"]![0]!.AsObject()).Should().Be(Id(observation));
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Observations")]
    public async Task InferredFeature_IsReusedByConcurrentObservations_AndLocationlessCreationRollsBack()
    {
        var location = await CreateAsync("Locations", Location("Concurrent sampling point"));
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Device", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(location))) });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var observations = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => CreateAsync("Observations", new JsonObject { ["result"] = index, ["Datastream"] = Reference(Id(stream)) })));
        var features = await Task.WhenAll(observations.Select(entity => ReadAsync($"Observations({Id(entity)})/FeatureOfInterest")));
        features.Select(Id).Distinct().Should().ContainSingle();
        var before = await ReadAsync("Things?$count=true&$top=0");
        using var admin = _fixture.CreateAdminClient();
        var invalid = Datastream(1);
        invalid["Thing"] = new JsonObject { ["name"] = "Must roll back", ["description"] = "Locationless" };
        invalid["Observations"] = new JsonArray(new JsonObject { ["result"] = 4 });
        using var response = await admin.PostAsync("/sta/v1.1/Datastreams", Body(invalid));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var after = await ReadAsync("Things?$count=true&$top=0");
        after["@iot.count"]!.GetValue<long>().Should().Be(before["@iot.count"]!.GetValue<long>());
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("PATCH /sta/v1.1/Datastreams({id})")]
    [Endpoint("DELETE /sta/v1.1/Locations({id})")]
    public async Task Patch_MergesComplexMembersAddsBindingsIgnoresId_AndLocationDeleteCascadesHistory()
    {
        var first = await CreateAsync("Locations", Location("First"));
        var alternate = Location("Second");
        alternate["encodingType"] = "application/vnd.honua.test-location+json";
        var second = await CreateAsync("Locations", alternate);
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Device", ["description"] = "Synthetic", ["properties"] = new JsonObject { ["keep"] = 1, ["change"] = 2 }, ["Locations"] = new JsonArray(Reference(Id(first))) });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        using var admin = _fixture.CreateAdminClient();
        using var patch = await admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["@iot.id"] = 999999, ["properties"] = new JsonObject { ["change"] = 3 }, ["Locations"] = new JsonArray(Reference(Id(second))) }));
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var updated = await ReadAsync($"Things({Id(thing)})");
        updated["properties"]!["keep"]!.GetValue<int>().Should().Be(1);
        updated["properties"]!["change"]!.GetValue<int>().Should().Be(3);
        var locations = await ReadAsync($"Things({Id(thing)})/Locations");
        locations["value"]!.AsArray().Count.Should().Be(2);
        using var units = await admin.PatchAsync($"/sta/v1.1/Datastreams({Id(stream)})", Body(new JsonObject { ["unitOfMeasurement"] = new JsonObject { ["symbol"] = "degC" } }));
        units.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var changedStream = await ReadAsync($"Datastreams({Id(stream)})");
        changedStream["unitOfMeasurement"]!["name"]!.GetValue<string>().Should().Be("Celsius");
        var history = await ReadAsync($"Locations({Id(first)})/HistoricalLocations");
        history["value"]!.AsArray().Should().NotBeEmpty();
        var historyIds = history["value"]!.AsArray().Select(node => Id(node!.AsObject())).ToArray();
        using var deleted = await admin.DeleteAsync($"/sta/v1.1/Locations({Id(first)})");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
        foreach (var id in historyIds)
        {
            using var response = await _fixture.Client.GetAsync($"/sta/v1.1/HistoricalLocations({id})");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Expansion_ContinuesPastOneHundred_AndProjectionRetainsExpandedEntities()
    {
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var stream = await CreateAsync("Datastreams", Datastream(1));
        using var admin = _fixture.CreateAdminClient();
        var observations = new JsonArray(Enumerable.Range(0, 101).Select(index => (JsonNode)new JsonObject { ["result"] = index, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) }).ToArray());
        using var response = await admin.PostAsync("/sta/v1.1/Observations", Body(new JsonObject { ["value"] = observations }));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var expanded = await ReadAsync($"Datastreams({Id(stream)})?$select=id&$expand=Thing,Observations($top=100;$count=true;$select=id,result)");
        expanded["Thing"].Should().NotBeNull();
        expanded["Observations"]!.AsArray().Count.Should().Be(100);
        expanded["Observations@iot.count"]!.GetValue<long>().Should().Be(101);
        using var next = await _fixture.Client.GetAsync(expanded["Observations@iot.nextLink"]!.GetValue<string>());
        next.StatusCode.Should().Be(HttpStatusCode.OK);
        var remaining = JsonNode.Parse(await next.Content.ReadAsStringAsync())!;
        remaining["value"]!.AsArray().Should().ContainSingle();
        remaining["value"]![0]!["result"]!.GetValue<int>().Should().Be(100);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/FeaturesOfInterest")]
    [Endpoint("POST /sta/v1.1/Locations")]
    [Endpoint("POST /sta/v1.1/Observations")]
    public async Task Validation_RejectsMalformedGeometryAndWrongMeasurementType_AndAllowsExplicitNullGeometry()
    {
        using var admin = _fixture.CreateAdminClient();
        var malformed = Location("Invalid");
        malformed["location"] = JsonNode.Parse("""{"type":"Point","coordinates":[1]}""");
        using var invalidGeometry = await admin.PostAsync("/sta/v1.1/Locations", Body(malformed));
        invalidGeometry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var feature = await CreateAsync("FeaturesOfInterest", new JsonObject { ["name"] = "Explicit unknown geometry", ["description"] = "Real identified feature without geometry", ["encodingType"] = "application/geo+json", ["feature"] = JsonNode.Parse("""{"type":"Feature","geometry":null,"properties":{}}""") });
        using var invalidType = await admin.PostAsync("/sta/v1.1/Observations", Body(new JsonObject { ["result"] = "not a measurement", ["Datastream"] = Reference(1), ["FeatureOfInterest"] = Reference(Id(feature)) }));
        invalidType.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var observation = await CreateAsync("Observations", new JsonObject { ["result"] = 7.5, ["Datastream"] = Reference(1), ["FeatureOfInterest"] = Reference(Id(feature)), ["phenomenonTime"] = "2026-01-01T00:00:00Z/2026-01-02T00:00:00Z" });
        observation["phenomenonTime"]!.GetValue<string>().Should().Contain("/");
        using var anonymousClient = _fixture.CreateClient();
        using var anonymous = await anonymousClient.PostAsync("/sta/v1.1/Locations", Body(Location("Unauthorized")));
        anonymous.StatusCode.Should().BeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1")]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task LegacyUnresolvedObservations_RemainReadableAndBlockConformantProfileActivation()
    {
        var legacy = await ReadAsync("Observations(1)");
        var root = await ReadAsync(string.Empty);
        root["serverSettings"]!["honua:unresolvedFeatureOfInterestCount"]!.GetValue<long>().Should().BeGreaterThan(0);
        var configuration = _fixture.GetService<IConfiguration>();
        var previous = configuration["SensorThings:ConformantProfile"];
        try
        {
            configuration["SensorThings:ConformantProfile"] = "true";
            using var blocked = await _fixture.Client.GetAsync("/sta/v1.1");
            blocked.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        }
        finally { configuration["SensorThings:ConformantProfile"] = previous; }
        var preserved = await ReadAsync("Observations(1)");
        JsonNode.DeepEquals(preserved["result"], legacy["result"]).Should().BeTrue();
        preserved["phenomenonTime"]!.GetValue<string>().Should().Be(legacy["phenomenonTime"]!.GetValue<string>());
        using var unresolved = await _fixture.Client.GetAsync("/sta/v1.1/Observations(1)/FeatureOfInterest");
        unresolved.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    public async Task TypedResults_PreserveJsonKindsAndMixedCollectionFilters_AndPatchClearsIntervalEnd()
    {
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var cases = new[]
        {
            (Type: "OM_Measurement", Result: "12.5"), (Type: "OM_CountObservation", Result: "7"),
            (Type: "OM_TruthObservation", Result: "true"), (Type: "OM_CategoryObservation", Result: "\"category\""),
            (Type: "OM_Observation", Result: "{\"sample\":[1,true,\"text\"]}")
        };
        var ids = new List<long>();
        foreach (var item in cases)
        {
            var body = Datastream(1);
            body["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/" + item.Type;
            var stream = await CreateAsync("Datastreams", body);
            var observation = await CreateAsync("Observations", new JsonObject { ["result"] = JsonNode.Parse(item.Result), ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature), ["phenomenonTime"] = "2026-01-01T00:00:00Z/2026-01-02T00:00:00Z" });
            JsonNode.DeepEquals(observation["result"], JsonNode.Parse(item.Result)).Should().BeTrue();
            ids.Add(Id(observation));
        }
        var truths = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("result eq true"));
        truths["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Equal(ids[2]);
        var categories = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("result eq 'category'"));
        categories["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Equal(ids[3]);
        var nullStreamBody = Datastream(1);
        nullStreamBody["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_Observation";
        var nullStream = await CreateAsync("Datastreams", nullStreamBody);
        var nullObservation = await CreateAsync("Observations", new JsonObject { ["result"] = null, ["Datastream"] = Reference(Id(nullStream)), ["FeatureOfInterest"] = Reference(feature) });
        var nulls = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("result eq null"));
        nulls["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Equal(Id(nullObservation));
        var nonNulls = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("result ne null") + "&$top=1000");
        nonNulls["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(ids).And.NotContain(Id(nullObservation));
        var notFalse = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("result ne false"));
        notFalse["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(ids[2]);
        var reversed = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString("20 lt result"));
        reversed["value"]!.AsArray().Should().OnlyContain(node => node!["result"]!.GetValue<double>() > 20);
        using var admin = _fixture.CreateAdminClient();
        using var response = await admin.PatchAsync($"/sta/v1.1/Observations({ids[0]})", Body(new JsonObject { ["phenomenonTime"] = "2026-01-03T00:00:00Z", ["result"] = 14.75 }));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var patched = await ReadAsync($"Observations({ids[0]})");
        patched["phenomenonTime"]!.GetValue<string>().Should().NotContain("/");
        patched["result"]!.GetValue<double>().Should().Be(14.75);
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Things")]
    public async Task NestedInlineComputedIdentifier_IsIgnoredAndInverseParentIsBound()
    {
        var nested = Datastream(1);
        nested.Remove("Thing");
        nested["@iot.id"] = 999999;
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Nested parent", ["description"] = "Synthetic", ["Datastreams"] = new JsonArray(nested) });
        var streams = await ReadAsync($"Things({Id(thing)})/Datastreams");
        streams["value"]!.AsArray().Should().ContainSingle();
        var stream = streams["value"]![0]!.AsObject();
        Id(stream).Should().NotBe(999999);
        Id(await ReadAsync($"Datastreams({Id(stream)})/Thing")).Should().Be(Id(thing));
        using var top = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=2147483648");
        top.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var skip = await _fixture.Client.GetAsync("/sta/v1.1/Things?$skip=2147483648");
        skip.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("POST /sta/v1.1/HistoricalLocations")]
    [Endpoint("PATCH /sta/v1.1/Locations({id})")]
    [Endpoint("PATCH /sta/v1.1/Sensors({id})")]
    public async Task History_OnlyNewestRecordChangesCurrentLocation_AndPatchUsesEffectiveEncoding()
    {
        var first = await CreateAsync("Locations", Location("First history location"));
        var second = await CreateAsync("Locations", Location("Second history location"));
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Historical device", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(first))) });
        await CreateAsync("HistoricalLocations", new JsonObject { ["time"] = "2040-01-01T00:00:00Z", ["Thing"] = Reference(Id(thing)), ["Locations"] = new JsonArray(Reference(Id(second))) });
        await CreateAsync("HistoricalLocations", new JsonObject { ["time"] = "2030-01-01T00:00:00Z", ["Thing"] = Reference(Id(thing)), ["Locations"] = new JsonArray(Reference(Id(first))) });
        await CreateAsync("HistoricalLocations", new JsonObject { ["time"] = "2040-01-01T00:00:00Z", ["Thing"] = Reference(Id(thing)), ["Locations"] = new JsonArray(Reference(Id(first))) });
        var current = await ReadAsync($"Things({Id(thing)})/Locations");
        current["value"]!.AsArray().Should().ContainSingle();
        Id(current["value"]![0]!.AsObject()).Should().Be(Id(second));
        using var admin = _fixture.CreateAdminClient();
        using var invalidGeometry = await admin.PatchAsync($"/sta/v1.1/Locations({Id(second)})", Body(new JsonObject { ["location"] = JsonNode.Parse("""{"type":"Point","coordinates":[1]}""") }));
        invalidGeometry.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var invalidMetadata = await admin.PatchAsync("/sta/v1.1/Sensors(1)", Body(new JsonObject { ["metadata"] = new JsonObject { ["invalid"] = true } }));
        invalidMetadata.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var emptyHistory = await admin.PostAsync("/sta/v1.1/HistoricalLocations", Body(new JsonObject { ["time"] = "2050-01-01T00:00:00Z", ["Thing"] = Reference(Id(thing)), ["Locations"] = new JsonArray() }));
        emptyHistory.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("DELETE /sta/v1.1/Observations({id})/$ref")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})/$ref")]
    [Endpoint("POST /sta/v1.1/Observations({id})")]
    [Endpoint("DELETE /sta/v1.1/Observations({id})/$value")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})/$value")]
    [Endpoint("GET /sta/v1.1/Observations({id})/$value")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Thing({id})")]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task ReferenceWritesAndBulkSingletonPost_DoNotMutateEntities()
    {
        var before = (await ReadAsync("Observations?$count=true"))["@iot.count"]!.GetValue<long>();
        using var admin = _fixture.CreateAdminClient();
        using var delete = await admin.DeleteAsync("/sta/v1.1/Observations(1)/$ref");
        delete.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        using var patch = await admin.PatchAsync("/sta/v1.1/Observations(1)/$ref", Body(new JsonObject { ["result"] = 99 }));
        patch.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        using var bulk = await admin.PostAsync("/sta/v1.1/Observations(1)", Body(new JsonObject { ["value"] = new JsonArray() }));
        bulk.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var rawDelete = await admin.DeleteAsync("/sta/v1.1/Observations(1)/$value");
        rawDelete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var rawPatch = await admin.PatchAsync("/sta/v1.1/Observations(1)/$value", Body(new JsonObject { ["result"] = 99 }));
        rawPatch.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var rawRead = await admin.GetAsync("/sta/v1.1/Observations(1)/$value");
        rawRead.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var singletonKey = await admin.GetAsync("/sta/v1.1/Datastreams(1)/Thing(999)");
        singletonKey.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync("Observations?$count=true"))["@iot.count"]!.GetValue<long>().Should().Be(before);
        await ReadAsync("Observations(1)");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things({id})/properties/site/name")]
    [Endpoint("GET /sta/v1.1/Things({id})/properties/site/name/$value")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/unitOfMeasurement/name")]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Thing/$ref")]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task ComplexPropertyPathsProjectionReferencesAndUnsupportedOptions_UseCoreResourceSemantics()
    {
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Complex device", ["description"] = "Synthetic", ["properties"] = JsonNode.Parse("""{"site":{"name":"Laboratory","optional":null,"keep":1}}""") });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var property = await ReadAsync($"Things({Id(thing)})/properties/site/name");
        property["name"]!.GetValue<string>().Should().Be("Laboratory");
        using var raw = await _fixture.Client.GetAsync($"/sta/v1.1/Things({Id(thing)})/properties/site/name/$value");
        raw.StatusCode.Should().Be(HttpStatusCode.OK);
        (await raw.Content.ReadAsStringAsync()).Should().Be("Laboratory");
        using var empty = await _fixture.Client.GetAsync($"/sta/v1.1/Things({Id(thing)})/properties/site/optional/$value");
        empty.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missing = await _fixture.Client.GetAsync($"/sta/v1.1/Things({Id(thing)})/properties/site/missing");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync($"Datastreams({Id(stream)})/unitOfMeasurement/name"))["name"]!.GetValue<string>().Should().Be("Celsius");
        var selected = await ReadAsync($"Things({Id(thing)})?$select=properties/site/name");
        selected["properties"]!["site"]!.AsObject().Count.Should().Be(1);
        selected["properties"]!["site"]!["name"]!.GetValue<string>().Should().Be("Laboratory");
        var unit = await ReadAsync($"Datastreams({Id(stream)})?$select=unitOfMeasurement/name");
        unit["unitOfMeasurement"]!.AsObject().Count.Should().Be(1);
        var reference = await ReadAsync($"Datastreams({Id(stream)})/Thing/$ref");
        reference["value"]!.AsArray().Should().ContainSingle();
        reference["value"]![0]!["@iot.selfLink"]!.GetValue<string>().Should().EndWith($"Things({Id(thing)})");
        using var unsupported = await _fixture.Client.GetAsync("/sta/v1.1/Things?$search=unsupported");
        unsupported.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    [Endpoint("GET /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Sensors")]
    public async Task SharedQueryExpressions_HandleTypedJsonNavigationFunctionsSpatialPredicatesAndOrderParameters()
    {
        var location = await CreateAsync("Locations", Location("Query location"));
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Query Lab", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(location))), ["properties"] = JsonNode.Parse("""{"floor":12,"flag":true,"site":{"name":"Lab"}}""") });
        var other = await CreateAsync("Things", new JsonObject { ["name"] = "Query Text", ["description"] = "Synthetic", ["properties"] = JsonNode.Parse("""{"floor":"12","flag":true,"site":null}""") });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var observation = await CreateAsync("Observations", new JsonObject { ["result"] = 25, ["phenomenonTime"] = "2026-05-01T00:00:00Z", ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) });
        var filters = new[] { "properties/floor eq 12", "contains(properties/site/name,'ab')", "Datastreams/Observations/result gt 20", $"Datastreams/Observations/FeatureOfInterest/id eq {feature}", "year(Datastreams/Observations/phenomenonTime) eq 2026", "geo.distance(Locations/location,geography'POINT(-157.8 21.3)') lt 1" };
        foreach (var filter in filters)
        {
            var matches = await ReadAsync("Things?$filter=" + Uri.EscapeDataString(filter));
            matches["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(Id(thing), filter).And.NotContain(Id(other));
        }
        var flags = await ReadAsync("Things?$filter=" + Uri.EscapeDataString("properties/flag eq true"));
        flags["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(Id(thing)).And.Contain(Id(other));
        var ordered = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"id ge {Id(thing)}") + "&$orderby=" + Uri.EscapeDataString("concat(name,',x') desc"));
        Id(ordered["value"]![0]!.AsObject()).Should().Be(Id(other));
        var jsonOrder = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"id ge {Id(thing)}") + "&$orderby=" + Uri.EscapeDataString("properties/site/name asc"));
        Id(jsonOrder["value"]![0]!.AsObject()).Should().Be(Id(other));
        var observationOrder = await ReadAsync("Observations?$filter=" + Uri.EscapeDataString($"id eq {Id(observation)}") + "&$orderby=" + Uri.EscapeDataString("year(phenomenonTime) desc,result add 3 asc,Datastream/id desc"));
        observationOrder["value"]!.AsArray().Should().ContainSingle();
        var sensor = await ReadAsync("Sensors?$filter=" + Uri.EscapeDataString("tolower(metadata) eq 'synthetic sensor'"));
        sensor["value"]!.AsArray().Should().NotBeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("PATCH /sta/v1.1/Locations({id})")]
    [Endpoint("GET /sta/v1.1/Things({id})/HistoricalLocations")]
    [Endpoint("GET /sta/v1.1/Things({id})/Locations")]
    public async Task LocationBindingsRequireDistinctEncodings_AndUnchangedBindingsDoNotInventHistory()
    {
        var first = await CreateAsync("Locations", Location("First encoding"));
        var duplicate = await CreateAsync("Locations", Location("Duplicate encoding"));
        var alternateBody = Location("Alternate encoding");
        alternateBody["encodingType"] = "application/vnd.honua.test-location+json";
        var alternate = await CreateAsync("Locations", alternateBody);
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Encoding device", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(first))) });
        using var admin = _fixture.CreateAdminClient();
        using var invalid = await admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["Locations"] = new JsonArray(Reference(Id(duplicate))) }));
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var valid = await admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["Locations"] = new JsonArray(Reference(Id(alternate))) }));
        valid.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var before = (await ReadAsync($"Things({Id(thing)})/HistoricalLocations?$count=true"))["@iot.count"]!.GetValue<long>();
        using var repeated = await admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["Locations"] = new JsonArray(Reference(Id(alternate))) }));
        repeated.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await ReadAsync($"Things({Id(thing)})/HistoricalLocations?$count=true"))["@iot.count"]!.GetValue<long>().Should().Be(before);
        using var collision = await admin.PatchAsync($"/sta/v1.1/Locations({Id(alternate)})", Body(new JsonObject { ["encodingType"] = "application/geo+json" }));
        collision.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync($"Things({Id(thing)})/Locations"))["value"]!.AsArray().Count.Should().Be(2);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task UnitlessDatastream_RoundTripsMandatoryNullUnitMembersAndSupportsNullFilter()
    {
        var body = Datastream(1);
        body["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_TruthObservation";
        body["unitOfMeasurement"] = new JsonObject { ["name"] = null, ["symbol"] = null, ["definition"] = null };
        var stream = await CreateAsync("Datastreams", body);
        var unit = stream["unitOfMeasurement"]!.AsObject();
        unit.Count.Should().Be(3);
        unit.Select(member => member.Value).Should().OnlyContain(value => value == null);
        var matches = await ReadAsync("Datastreams?$filter=" + Uri.EscapeDataString("unitOfMeasurement/name eq null"));
        matches["value"]!.AsArray().Select(node => Id(node!.AsObject())).Should().Contain(Id(stream));
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Datastreams({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    [Endpoint("GET /sta/v1.1/Observations({id})/Datastream")]
    public async Task ObservationReparentAndDatastreamTypePatch_RejectIncompatibleExistingResultsAtomically()
    {
        var categoryBody = Datastream(1);
        categoryBody["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_CategoryObservation";
        var category = await CreateAsync("Datastreams", categoryBody);
        var measurement = await CreateAsync("Datastreams", Datastream(1));
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var observation = await CreateAsync("Observations", new JsonObject { ["result"] = "cloudy", ["Datastream"] = Reference(Id(category)), ["FeatureOfInterest"] = Reference(feature) });
        using var admin = _fixture.CreateAdminClient();
        using var reparent = await admin.PatchAsync($"/sta/v1.1/Datastreams({Id(measurement)})", Body(new JsonObject { ["name"] = "Must roll back", ["Observations"] = new JsonArray(Reference(Id(observation))) }));
        reparent.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync($"Datastreams({Id(measurement)})"))["name"]!.GetValue<string>().Should().Be("Measurement");
        Id(await ReadAsync($"Observations({Id(observation)})/Datastream")).Should().Be(Id(category));
        using var typeChange = await admin.PatchAsync($"/sta/v1.1/Datastreams({Id(category)})", Body(new JsonObject { ["observationType"] = measurement["observationType"]!.DeepClone() }));
        typeChange.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync($"Datastreams({Id(category)})"))["observationType"]!.GetValue<string>().Should().EndWith("OM_CategoryObservation");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task SlashExpansionsMergeCommonPrefixes_AndNestedUnsupportedOptionsRetain501()
    {
        var stream = await CreateAsync("Datastreams", Datastream(1));
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        await CreateAsync("Observations", new JsonObject { ["result"] = 1, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) });
        var thing = await ReadAsync("Things(1)?$expand=Datastreams/ObservedProperty,Datastreams/Observations/FeatureOfInterest");
        var expanded = thing["Datastreams"]!.AsArray().Single(node => Id(node!.AsObject()) == Id(stream))!.AsObject();
        expanded["ObservedProperty"]!.AsObject()["definition"].Should().NotBeNull();
        Id(expanded["Observations"]![0]!["FeatureOfInterest"]!.AsObject()).Should().Be(feature);
        var selected = await ReadAsync($"Datastreams({Id(stream)})?$select=id&$expand=Observations/FeatureOfInterest");
        selected["Observations"]!.AsArray().Should().ContainSingle();
        using var unsupported = await _fixture.Client.GetAsync("/sta/v1.1/Things(1)?$expand=Datastreams($expand=Observations($search=x))");
        unsupported.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Locations({id})")]
    [Endpoint("GET /sta/v1.1/FeaturesOfInterest({id})")]
    [Endpoint("GET /sta/v1.1/Locations({id})/properties/site/name")]
    [Endpoint("GET /sta/v1.1/FeaturesOfInterest({id})/properties/site/name")]
    public async Task JsonPropertiesOnLocationsAndFeatures_SupportNestedSelectAndPropertyReads()
    {
        foreach (var set in new[] { "Locations", "FeaturesOfInterest" })
        {
            var body = Location("Nested " + set);
            if (set == "FeaturesOfInterest") { body["feature"] = body["location"]!.DeepClone(); body.Remove("location"); }
            body["properties"] = JsonNode.Parse("""{"site":{"name":"Harbour","floor":2},"unselected":true}""");
            var entity = await CreateAsync(set, body);
            var selected = await ReadAsync($"{set}({Id(entity)})?$select=properties/site/name");
            selected["properties"]!["site"]!.AsObject().Should().ContainSingle();
            selected["properties"]!["site"]!["name"]!.GetValue<string>().Should().Be("Harbour");
            var property = await ReadAsync($"{set}({Id(entity)})/properties/site/name");
            property["name"]!.GetValue<string>().Should().Be("Harbour");
        }
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Locations")]
    [Endpoint("POST /sta/v1.1/Things({id})/Locations")]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("DELETE /sta/v1.1/Things({id})")]
    [Endpoint("PATCH /sta/v1.1/Datastreams({id})/Thing")]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    public async Task AnonymousWritesAreDeniedOnLiteralAndCatchAllRoutes_BeforeValidationOrMutation()
    {
        using var anonymous = _fixture.CreateClient();
        foreach (var (method, path) in new[] { (HttpMethod.Post, "Locations"), (HttpMethod.Post, "Things(1)/Locations"), (HttpMethod.Patch, "Things(1)"), (HttpMethod.Delete, "Things(1)"), (HttpMethod.Patch, "Datastreams(1)/Thing") })
        {
            using var request = new HttpRequestMessage(method, "/sta/v1.1/" + path) { Content = new StringContent("{", Encoding.UTF8, "application/json") };
            using var response = await anonymous.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, method + " " + path);
        }
        (await ReadAsync("Things(1)"))["@iot.id"]!.GetValue<long>().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    [Endpoint("GET /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Locations")]
    public async Task MandatoryTable23Functions_ExecuteAgainstTypedCatalogTimeResultAndGeometry()
    {
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Functions", ["description"] = "Sensor Things" });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var observation = await CreateAsync("Observations", new JsonObject { ["result"] = 32.6, ["phenomenonTime"] = "2026-01-02T12:34:56.25Z", ["resultTime"] = "2026-01-02T12:34:56.25Z", ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) });
        string[] strings = ["substringof('Sensor Things',description)", "endswith(description,'Things')", "startswith(description,'Sensor')", "length(description) eq 13", "indexof(description,'Things') eq 7", "substring(description,1) eq 'ensor Things'", "substring(description,2,4) eq 'nsor'", "tolower(description) eq 'sensor things'", "toupper(description) eq 'SENSOR THINGS'", "trim(concat(' ',concat(description,' '))) eq 'Sensor Things'", "concat(name,description) eq 'FunctionsSensor Things'"];
        string[] timesAndMath = ["year(resultTime) eq 2026", "month(resultTime) eq 1", "day(resultTime) eq 2", "hour(resultTime) eq 12", "minute(resultTime) eq 34", "second(resultTime) eq 56", "fractionalseconds(resultTime) eq 0.25", "date(resultTime) eq date(phenomenonTime)", "time(resultTime) eq time(phenomenonTime)", "totaloffsetminutes(resultTime) eq 0", "resultTime lt now()", "resultTime gt mindatetime()", "resultTime lt maxdatetime()", "round(result) eq 33", "floor(result) eq 32", "ceiling(result) eq 33"];
        foreach (var (set, id, filters) in new[] { ("Things", Id(thing), strings), ("Observations", Id(observation), timesAndMath) })
            foreach (var filter in filters)
            {
                var page = await ReadAsync(set + "?$filter=" + Uri.EscapeDataString($"id eq {id} and ({filter})"));
                page["value"]!.AsArray().Should().ContainSingle(filter);
            }
        var locationBody = Location("Spatial functions");
        locationBody["location"] = JsonNode.Parse("""{"type":"Point","coordinates":[30,10]}""");
        var location = await CreateAsync("Locations", locationBody);
        var point = "geography'POINT(30 10)'";
        var polygon = "geography'POLYGON((29 9,31 9,31 11,29 11,29 9))'";
        string[] spatial = [$"geo.distance(location,{point}) eq 0", "geo.length(geography'LINESTRING(30 10,31 10)') gt 0", $"geo.intersects(location,{polygon})", $"st_equals(location,{point})", $"not st_disjoint(location,{polygon})", $"not st_touches(location,{polygon})", $"st_within(location,{polygon})", $"not st_overlaps(location,{polygon})", $"not st_crosses(location,{polygon})", $"st_intersects(location,{polygon})", $"st_contains(location,{point})", $"st_relate(location,{point},'T********')"];
        foreach (var filter in spatial)
        {
            var page = await ReadAsync("Locations?$filter=" + Uri.EscapeDataString($"id eq {Id(location)} and ({filter})"));
            page["value"]!.AsArray().Should().ContainSingle(filter);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Locations({id})")]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("GET /sta/v1.1/Things({id})/Locations")]
    public async Task ConcurrentLocationEncodingPatchAndThingBinding_CannotCommitDuplicateCurrentEncodings()
    {
        using var admin = _fixture.CreateAdminClient();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var first = await CreateAsync("Locations", Location("Current " + attempt));
            var candidateBody = Location("Candidate " + attempt);
            candidateBody["encodingType"] = "application/vnd.honua.test-location+json";
            var candidate = await CreateAsync("Locations", candidateBody);
            var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Concurrent " + attempt, ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(first))) });
            var patchEncoding = admin.PatchAsync($"/sta/v1.1/Locations({Id(candidate)})", Body(new JsonObject { ["encodingType"] = "application/geo+json" }));
            var bind = admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["Locations"] = new JsonArray(Reference(Id(candidate))) }));
            var responses = await Task.WhenAll(patchEncoding, bind);
            try { responses.Select(response => response.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest }); }
            finally { foreach (var response in responses) response.Dispose(); }
            var locations = (await ReadAsync($"Things({Id(thing)})/Locations"))["value"]!.AsArray();
            locations.Select(location => location!["encodingType"]!.GetValue<string>()).Should().OnlyHaveUniqueItems();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    [Endpoint("DELETE /sta/v1.1/Observations({id})")]
    public async Task DatastreamExtents_UseValidatedRelationsFullIntervalsAndActualFeatureBounds()
    {
        var body = Datastream(1);
        body["observedArea"] = JsonNode.Parse("""{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[1,0],[0,0]]]}""");
        var stream = await CreateAsync("Datastreams", body);
        var empty = await ReadAsync($"Datastreams({Id(stream)})");
        empty.ContainsKey("phenomenonTime").Should().BeFalse();
        empty.ContainsKey("resultTime").Should().BeFalse();
        empty.ContainsKey("observedArea").Should().BeFalse();
        var observations = new List<long>();
        foreach (var (coordinate, time, resultTime) in new[] { ("[10,20]", "2026-01-01T00:00:00Z/2026-01-04T00:00:00Z", "2026-01-02T00:00:00Z"), ("[30,40]", "2026-01-03T00:00:00Z", "2026-01-05T00:00:00Z") })
        {
            var feature = await CreateAsync("FeaturesOfInterest", new JsonObject { ["name"] = "Bounds " + coordinate, ["description"] = "Synthetic", ["encodingType"] = "application/geo+json", ["feature"] = JsonNode.Parse("{\"type\":\"Point\",\"coordinates\":" + coordinate + "}") });
            observations.Add(Id(await CreateAsync("Observations", new JsonObject { ["result"] = 1, ["phenomenonTime"] = time, ["resultTime"] = resultTime, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(Id(feature)) })));
        }
        var populated = await ReadAsync($"Datastreams({Id(stream)})?$select=phenomenonTime,resultTime,observedArea");
        var phenomenon = populated["phenomenonTime"]!.GetValue<string>().Split('/').Select(ParseInstant).ToArray();
        phenomenon.Should().BeEquivalentTo(new[] { ParseInstant("2026-01-01T00:00:00Z"), ParseInstant("2026-01-04T00:00:00Z") }, options => options.WithStrictOrdering());
        var result = populated["resultTime"]!.GetValue<string>().Split('/').Select(ParseInstant).ToArray();
        result.Should().BeEquivalentTo(new[] { ParseInstant("2026-01-02T00:00:00Z"), ParseInstant("2026-01-05T00:00:00Z") }, options => options.WithStrictOrdering());
        populated["observedArea"]!["type"]!.GetValue<string>().Should().Be("Polygon");
        populated["observedArea"]!["coordinates"]![0]!.ToJsonString().Should().Be("[[10,20],[10,40],[30,40],[30,20],[10,20]]");
        var filtered = await ReadAsync("Datastreams?$filter=" + Uri.EscapeDataString($"id eq {Id(stream)} and st_contains(observedArea,geography'POINT(20 30)')"));
        filtered["value"]!.AsArray().Should().ContainSingle("filter geometry must match the computed response geometry");
        using var admin = _fixture.CreateAdminClient();
        using var patch = await admin.PatchAsync($"/sta/v1.1/Observations({observations[0]})", Body(new JsonObject { ["phenomenonTime"] = "2026-01-01T00:00:00Z/2026-01-02T00:00:00Z" }));
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var changed = (await ReadAsync($"Datastreams({Id(stream)})"))["phenomenonTime"]!.GetValue<string>().Split('/');
        ParseInstant(changed[1]).Should().Be(ParseInstant("2026-01-03T00:00:00Z"));
        var other = await CreateAsync("Datastreams", Datastream(1));
        using var reparent = await admin.PatchAsync($"/sta/v1.1/Observations({observations[1]})", Body(new JsonObject { ["Datastream"] = Reference(Id(other)) }));
        reparent.StatusCode.Should().Be(HttpStatusCode.NoContent);
        changed = (await ReadAsync($"Datastreams({Id(stream)})"))["phenomenonTime"]!.GetValue<string>().Split('/');
        ParseInstant(changed[1]).Should().Be(ParseInstant("2026-01-02T00:00:00Z"));
        using var delete = await admin.DeleteAsync($"/sta/v1.1/Observations({observations[0]})");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cleared = await ReadAsync($"Datastreams({Id(stream)})");
        cleared.ContainsKey("phenomenonTime").Should().BeFalse();
        cleared.ContainsKey("resultTime").Should().BeFalse();
        cleared.ContainsKey("observedArea").Should().BeFalse("a supplied stale area must not resurface after the last observation disappears");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Observations")]
    public async Task LargeCountResults_PreserveAdjacentIntegersInFiltersArithmeticAndOrdering()
    {
        var body = Datastream(1);
        body["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_CountObservation";
        var stream = await CreateAsync("Datastreams", body);
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        const long lower = 9007199254740992;
        const long upper = 9007199254740993;
        var ids = new List<long>();
        foreach (var value in new[] { upper, lower })
            ids.Add(Id(await CreateAsync("Observations", new JsonObject { ["result"] = value, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) })));
        foreach (var filter in new[] { $"result eq {upper}", $"result gt {lower}", $"result sub {lower} eq 1" })
        {
            var page = await ReadAsync($"Datastreams({Id(stream)})/Observations?$filter=" + Uri.EscapeDataString(filter));
            page["value"]!.AsArray().Should().ContainSingle(filter);
            page["value"]![0]!["@iot.id"]!.GetValue<long>().Should().Be(ids[0]);
            page["value"]![0]!["result"]!.GetValue<long>().Should().Be(upper);
        }
        foreach (var order in new[] { "result", "result add 0" })
        {
            var page = await ReadAsync($"Datastreams({Id(stream)})/Observations?$orderby=" + Uri.EscapeDataString(order));
            page["value"]!.AsArray().Select(item => item!["result"]!.GetValue<long>()).Should().Equal(lower, upper);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task ComparisonsAcrossCollectionNavigations_RequireMatchingRelatedEntities()
    {
        var location = await CreateAsync("Locations", Location("Shared name"));
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Cross collection", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(location))) });
        var body = Datastream(Id(thing));
        body["name"] = "Shared name";
        await CreateAsync("Datastreams", body);
        var other = await CreateAsync("Things", new JsonObject { ["name"] = "No match", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(location))) });
        body = Datastream(Id(other));
        body["name"] = "Different name";
        await CreateAsync("Datastreams", body);
        foreach (var filter in new[] { "Datastreams/name eq Locations/name", "Locations/name eq Datastreams/name" })
        {
            var page = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"(id eq {Id(thing)} or id eq {Id(other)}) and ({filter})"));
            page["value"]!.AsArray().Should().ContainSingle(filter);
            page["value"]![0]!["@iot.id"]!.GetValue<long>().Should().Be(Id(thing));
        }
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Datastreams({id})")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task ConcurrentObservationResultAndDatastreamTypePatches_SerializeValidationWithoutDeadlock()
    {
        using var admin = _fixture.CreateAdminClient();
        var feature = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var stream = await CreateAsync("Datastreams", Datastream(1));
            var observation = await CreateAsync("Observations", new JsonObject { ["result"] = 1, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(feature) });
            var changeType = admin.PatchAsync($"/sta/v1.1/Datastreams({Id(stream)})", Body(new JsonObject { ["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_CountObservation" }));
            var changeResult = admin.PatchAsync($"/sta/v1.1/Observations({Id(observation)})", Body(new JsonObject { ["result"] = 1.5 }));
            var responses = await Task.WhenAll(changeType, changeResult).WaitAsync(TimeSpan.FromSeconds(30));
            try { responses.Select(response => response.StatusCode).Should().BeEquivalentTo(new[] { HttpStatusCode.NoContent, HttpStatusCode.BadRequest }); }
            finally { foreach (var response in responses) response.Dispose(); }
            var finalStream = await ReadAsync($"Datastreams({Id(stream)})");
            var finalObservation = await ReadAsync($"Observations({Id(observation)})");
            var count = finalStream["observationType"]!.GetValue<string>().EndsWith("OM_CountObservation", StringComparison.Ordinal);
            finalObservation["result"]!.GetValue<double>().Should().Be(count ? 1 : 1.5);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("PATCH /sta/v1.1/Things({id})")]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task JsonPrimitivePropertyComparisons_RetainTypesWithinAndAcrossCollectionScopes()
    {
        var locationBody = Location("JSON comparison");
        locationBody["properties"] = JsonNode.Parse("""{"label":"Shared","enabled":true}""");
        var location = await CreateAsync("Locations", locationBody);
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "JSON comparison", ["description"] = "Synthetic", ["properties"] = JsonNode.Parse("""{"a":"Shared","b":"Shared","label":"JSON comparison","yes":true,"alsoYes":true,"textYes":"true","empty":null}"""), ["Locations"] = new JsonArray(Reference(Id(location))) });
        var streamBody = Datastream(Id(thing));
        streamBody["properties"] = JsonNode.Parse("""{"label":"Shared","enabled":true}""");
        await CreateAsync("Datastreams", streamBody);
        using var admin = _fixture.CreateAdminClient();
        using var patch = await admin.PatchAsync($"/sta/v1.1/Things({Id(thing)})", Body(new JsonObject { ["properties"] = new JsonObject { ["identifier"] = Id(thing) } }));
        patch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        foreach (var filter in new[] { "properties/a eq properties/b", "properties/yes eq properties/alsoYes", "properties/yes ne properties/textYes", "properties/label eq name", "name eq properties/label", "properties/identifier eq id", "id eq properties/identifier", "properties/empty eq properties/missing", "properties/missing eq properties/alsoMissing", "properties/a ne properties/empty", "properties/empty ne name", "Datastreams/properties/label eq Locations/properties/label", "Locations/properties/enabled eq Datastreams/properties/enabled" })
        {
            var page = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"id eq {Id(thing)} and ({filter})"));
            page["value"]!.AsArray().Should().ContainSingle(filter);
        }
        var mismatch = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"id eq {Id(thing)} and properties/yes eq properties/textYes"));
        mismatch["value"]!.AsArray().Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task SpatialComparisonAcrossCollections_UsesEachOriginalGeometryScope()
    {
        var location = await CreateAsync("Locations", Location("Spatial comparison"));
        var thing = await CreateAsync("Things", new JsonObject { ["name"] = "Spatial comparison", ["description"] = "Synthetic", ["Locations"] = new JsonArray(Reference(Id(location))) });
        var stream = await CreateAsync("Datastreams", Datastream(Id(thing)));
        var feature = await CreateAsync("FeaturesOfInterest", new JsonObject { ["name"] = "Matching feature", ["description"] = "Synthetic", ["encodingType"] = "application/geo+json", ["feature"] = location["location"]!.DeepClone() });
        await CreateAsync("Observations", new JsonObject { ["result"] = 1, ["Datastream"] = Reference(Id(stream)), ["FeatureOfInterest"] = Reference(Id(feature)) });
        foreach (var filter in new[] { "st_equals(Locations/location,Datastreams/Observations/FeatureOfInterest/feature)", "st_equals(Datastreams/Observations/FeatureOfInterest/feature,Locations/location)" })
        {
            var page = await ReadAsync("Things?$filter=" + Uri.EscapeDataString($"id eq {Id(thing)} and ({filter})"));
            page["value"]!.AsArray().Should().ContainSingle(filter);
        }
    }
}
