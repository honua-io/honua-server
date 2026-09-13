// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>HTTP navigation receipts for the five exposed SensorThings entity sets.</summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsNavigationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<JsonDocument> GetAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0}", path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things({id})/Datastreams")]
    [Endpoint("GET /sta/v1.1/Sensors({id})/Datastreams")]
    [Endpoint("GET /sta/v1.1/ObservedProperties({id})/Datastreams")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Thing")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Sensor")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/ObservedProperty")]
    [Endpoint("GET /sta/v1.1/Observations({id})/Datastream")]
    public async Task Navigation_FollowEveryEmittedLink_ReturnsRelatedSeededEntities()
    {
        // These identities and values are defined by migration 059's fixture.
        var expectedNames = new Dictionary<string, string>
        {
            ["Things"] = "Demo Air Quality Station",
            ["Sensors"] = "Demo Thermometer",
            ["ObservedProperties"] = "Air Temperature"
        };
        foreach (var (entitySet, name) in expectedNames)
        {
            using var entity = await GetAsync($"/sta/v1.1/{entitySet}(1)");
            entity.RootElement.GetProperty("name").GetString().Should().Be(name);
            using var related = await GetAsync(entity.RootElement.GetProperty("Datastreams@iot.navigationLink").GetString()!);
            related.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => item.GetProperty("@iot.id").GetInt64()).Should().Equal(1L);
        }

        using var stream = await GetAsync("/sta/v1.1/Datastreams(1)");
        foreach (var (navigation, entitySet) in new[] { ("Thing", "Things"), ("Sensor", "Sensors"), ("ObservedProperty", "ObservedProperties") })
        {
            using var related = await GetAsync(stream.RootElement.GetProperty(navigation + "@iot.navigationLink").GetString()!);
            related.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(1);
            related.RootElement.GetProperty("name").GetString().Should().Be(expectedNames[entitySet]);
            using var self = await GetAsync(related.RootElement.GetProperty("@iot.selfLink").GetString()!);
            self.RootElement.GetProperty("name").GetString().Should().Be(expectedNames[entitySet]);
        }

        using var observations = await GetAsync(stream.RootElement.GetProperty("Observations@iot.navigationLink").GetString()!);
        observations.RootElement.GetProperty("value").GetArrayLength().Should().Be(48);
        foreach (var observation in observations.RootElement.GetProperty("value").EnumerateArray())
        {
            var id = observation.GetProperty("@iot.id").GetInt32();
            observation.GetProperty("result").GetDouble().Should().BeApproximately(15 + (10 * Math.Sin(id)), 1e-9);
            observation.GetProperty("phenomenonTime").GetDateTimeOffset().Should()
                .Be(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddHours(id));
            observation.TryGetProperty("FeatureOfInterest@iot.navigationLink", out _).Should().BeFalse();
            using var related = await GetAsync(observation.GetProperty("Datastream@iot.navigationLink").GetString()!);
            related.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(1);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things({id})/Datastreams")]
    [Endpoint("GET /sta/v1.1/Sensors({id})/Datastreams")]
    [Endpoint("GET /sta/v1.1/ObservedProperties({id})/Datastreams")]
    public async Task RelatedDatastreams_QueryOptions_ApplyWithinRelationshipBeforePagingAndCount()
    {
        using var admin = _fixture.CreateAdminClient();
        var createdIds = new List<long>();
        foreach (var name in new[] { "Navigation Alpha", "Navigation Zulu", "Unrelated" })
        {
            var relation = name == "Unrelated" ? "{\"name\":\"Other\",\"description\":\"other\"}" : "{\"@iot.id\":1}";
            using var body = new StringContent($$"""
                {"name":"{{name}}","description":"navigation fixture",
                 "unitOfMeasurement":{"name":"degree Celsius","symbol":"C","definition":"urn:unit:celsius"},
                 "Thing":{{relation}},"Sensor":{{relation}},"ObservedProperty":{{relation}}}
                """, Encoding.UTF8, "application/json");
            using var response = await admin.PostAsync("/sta/v1.1/Datastreams", body);
            response.StatusCode.Should().Be(HttpStatusCode.Created);
            using var entity = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            createdIds.Add(entity.RootElement.GetProperty("@iot.id").GetInt64());
        }

        foreach (var path in new[] { "/sta/v1.1/Things(1)/Datastreams", "/sta/v1.1/Sensors(1)/Datastreams", "/sta/v1.1/ObservedProperties(1)/Datastreams" })
        {
            // The unrelated row also matches the OR filter: the relationship must AND
            // the entire expression, and its parameter must not collide with filter literals.
            using var first = await GetAsync(path + "?$filter=" + Uri.EscapeDataString("name ne 'Demo Air Temperature' or name eq 'Unrelated'") + "&$orderby=name%20desc&$top=1&$count=true&$select=id,name");
            first.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(2);
            first.RootElement.GetProperty("value")[0].GetProperty("@iot.id").GetInt64().Should().Be(createdIds[1]);
            first.RootElement.GetProperty("value")[0].EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo("@iot.id", "name");
            var next = first.RootElement.GetProperty("@iot.nextLink").GetString()!;
            next.Should().Contain(path);
            using var second = await GetAsync(next);
            second.RootElement.GetProperty("value")[0].GetProperty("@iot.id").GetInt64().Should().Be(createdIds[0]);
            second.RootElement.TryGetProperty("@iot.nextLink", out _).Should().BeFalse();
            using var empty = await GetAsync(path + "?$filter=name%20eq%20'Unrelated'&$count=true");
            empty.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(0);
            empty.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
            using var expanded = await GetAsync(path + "?$expand=Thing&$top=1");
            expanded.RootElement.GetProperty("value")[0].GetProperty("Thing").GetProperty("@iot.id").GetInt64().Should().Be(1);
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations({id})/Datastream")]
    public async Task Navigation_DistinctForeignKeys_ResolveTargetsAndOmitUnsupportedFeatureOfInterest()
    {
        using var admin = _fixture.CreateAdminClient();
        using var body = new StringContent("""
            {"name":"Distinct keys","description":"foreign key fixture",
             "unitOfMeasurement":{"name":"metre","symbol":"m","definition":"urn:unit:metre"},
             "Thing":{"@iot.id":701,"name":"Station 701","description":"station"},
             "Sensor":{"@iot.id":702,"name":"Sensor 702","description":"sensor"},
             "ObservedProperty":{"@iot.id":703,"name":"Property 703","description":"property"}}
            """, Encoding.UTF8, "application/json");
        using var created = await admin.PostAsync("/sta/v1.1/Datastreams", body);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var stream = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var streamId = stream.RootElement.GetProperty("@iot.id").GetInt64();
        foreach (var (navigation, entitySet, expectedId) in new[] { ("Thing", "Things", 701L), ("Sensor", "Sensors", 702L), ("ObservedProperty", "ObservedProperties", 703L) })
        {
            using var related = await GetAsync(stream.RootElement.GetProperty(navigation + "@iot.navigationLink").GetString()!);
            related.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(expectedId);
            using var reverse = await GetAsync($"/sta/v1.1/{entitySet}({expectedId})/Datastreams");
            reverse.RootElement.GetProperty("value").EnumerateArray()
                .Select(item => item.GetProperty("@iot.id").GetInt64()).Should().Equal(streamId);
        }
        using var observationBody = new StringContent($$"""
            {"phenomenonTime":"2026-08-01T00:00:00Z","result":42.25,
             "Datastream":{"@iot.id":{{streamId}}}
            }
            """, Encoding.UTF8, "application/json");
        using var observationResponse = await admin.PostAsync("/sta/v1.1/Observations", observationBody);
        observationResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        using var observation = JsonDocument.Parse(await observationResponse.Content.ReadAsStringAsync());
        observation.RootElement.TryGetProperty("FeatureOfInterest@iot.navigationLink", out _).Should().BeFalse();
        // The HTTP ingest surface does not accept FeatureOfInterest. Seed the nullable
        // storage field directly to exercise both mapper branches from the original defect.
        await using (var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema))
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sta_observation SET feature_of_interest_id = 987 WHERE id = @id";
            command.Parameters.AddWithValue("id", observation.RootElement.GetProperty("@iot.id").GetInt64());
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }
        using var read = await GetAsync(observation.RootElement.GetProperty("@iot.selfLink").GetString()!);
        read.RootElement.GetProperty("result").GetDouble().Should().Be(42.25);
        read.RootElement.TryGetProperty("FeatureOfInterest@iot.navigationLink", out _).Should().BeFalse();
        using var target = await GetAsync(read.RootElement.GetProperty("Datastream@iot.navigationLink").GetString()!);
        target.RootElement.GetProperty("@iot.id").GetInt64().Should().Be(streamId);
        target.RootElement.GetProperty("name").GetString().Should().Be("Distinct keys");
        target.RootElement.GetProperty("unitOfMeasurement").GetProperty("symbol").GetString().Should().Be("m");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations({id})/Datastream")]
    public async Task Navigation_MissingParentsAndUnsupportedOptions_ReturnExplicitErrors()
    {
        foreach (var path in new[] { "Things(999999)/Datastreams", "Sensors(999999)/Datastreams", "ObservedProperties(999999)/Datastreams", "Datastreams(999999)/Thing", "Datastreams(999999)/Sensor", "Datastreams(999999)/ObservedProperty", "Observations(999999)/Datastream" })
        {
            using var response = await _fixture.Client.GetAsync("/sta/v1.1/" + path);
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        using var selected = await GetAsync("/sta/v1.1/Observations(1)/Datastream?$select=name,Thing&$expand=Thing");
        selected.RootElement.GetProperty("Thing").GetProperty("@iot.id").GetInt64().Should().Be(1);
        foreach (var path in new[] { "Datastreams(1)/Thing", "Datastreams(1)/Sensor", "Datastreams(1)/ObservedProperty", "Observations(1)/Datastream" })
        {
            foreach (var option in new[] { "$filter=id%20eq%201", "$orderby=name", "$top=0", "$skip=1", "$count=true" })
            {
                using var invalid = await _fixture.Client.GetAsync($"/sta/v1.1/{path}?{option}");
                invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest, "{0} cannot apply to {1}", option, path);
            }
        }
        foreach (var query in new[] { "$select=FeatureOfInterest", "$expand=FeatureOfInterest" })
        {
            using var unavailable = await _fixture.Client.GetAsync("/sta/v1.1/Observations(1)?" + query);
            unavailable.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
            var error = await unavailable.Content.ReadAsStringAsync();
            error.Should().Contain("FeaturesOfInterest are not exposed");
            error.Should().NotContain("follow the entity");
        }
        using var unsupported = await _fixture.Client.GetAsync("/sta/v1.1/Things(1)/Datastreams?$expand=Observations($select=result)");
        unsupported.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }
}
