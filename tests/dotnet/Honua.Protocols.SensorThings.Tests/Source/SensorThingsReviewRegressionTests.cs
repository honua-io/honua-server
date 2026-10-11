// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Core.Features.SensorThings.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>Regressions for the SensorThings review findings on delete conflicts, counts, features, links, depth, and observation identity.</summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsReviewRegressionTests : IAsyncLifetime
{
    private const long DuplicateObservationId = 8800001;
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    private static long Id(JsonObject entity) => entity["@iot.id"]!.GetValue<long>();

    private async Task<JsonObject> CreatedAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.Created, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task InsertDuplicateObservationAsync(DateTimeOffset phenomenonTime, double result)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sta_observation (id, datastream_id, phenomenon_time, result)
            VALUES (@id, 1, @phenomenonTime, @result)
            """;
        command.Parameters.AddWithValue("id", DuplicateObservationId);
        command.Parameters.AddWithValue("phenomenonTime", phenomenonTime);
        command.Parameters.AddWithValue("result", result);
        (await command.ExecuteNonQueryAsync()).Should().Be(1);
    }

    private async Task<IReadOnlyList<double>> ObservationResultsAsync(long id)
    {
        await using var connection = await _fixture.Postgres.GetConnectionAsync(_fixture.CurrentSchema);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result FROM sta_observation WHERE id = @id ORDER BY phenomenon_time";
        command.Parameters.AddWithValue("id", id);
        var results = new List<double>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) results.Add(reader.GetDouble(0));
        return results;
    }

    [IntegrationTest]
    [Operation(Operations.Delete)]
    [Endpoint("POST /sta/v1.1/Things")]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Sensor")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/ObservedProperty")]
    [Endpoint("DELETE /sta/v1.1/Things({id})")]
    [Endpoint("DELETE /sta/v1.1/Sensors({id})")]
    [Endpoint("DELETE /sta/v1.1/ObservedProperties({id})")]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    [Endpoint("GET /sta/v1.1/Sensors({id})")]
    [Endpoint("GET /sta/v1.1/ObservedProperties({id})")]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Delete_ReferencedThingSensorOrObservedProperty_ConflictsAndKeepsRows()
    {
        using var admin = _fixture.CreateAdminClient();
        using var createdThing = await admin.PostAsync("/sta/v1.1/Things", Json(new JsonObject
        {
            ["name"] = "Referenced review thing",
            ["description"] = "Owned by a datastream"
        }));
        var thingId = Id(await CreatedAsync(createdThing));
        using var createdStream = await admin.PostAsync("/sta/v1.1/Datastreams", Json(new JsonObject
        {
            ["name"] = "Referenced review stream",
            ["description"] = "Holds the foreign keys from migration 059",
            ["unitOfMeasurement"] = new JsonObject { ["name"] = "Celsius", ["symbol"] = "C", ["definition"] = "https://example.test/celsius" },
            ["Thing"] = new JsonObject { ["@iot.id"] = thingId },
            ["Sensor"] = new JsonObject { ["name"] = "Referenced sensor", ["description"] = "Synthetic", ["encodingType"] = "text/plain", ["metadata"] = "Synthetic sensor" },
            ["ObservedProperty"] = new JsonObject { ["name"] = "Referenced property", ["description"] = "Synthetic", ["definition"] = "https://example.test/referenced" }
        }));
        var streamId = Id(await CreatedAsync(createdStream));

        using var sensorResponse = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({streamId})/Sensor");
        sensorResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var sensorId = Id(JsonNode.Parse(await sensorResponse.Content.ReadAsStringAsync())!.AsObject());
        using var propertyResponse = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({streamId})/ObservedProperty");
        propertyResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var propertyId = Id(JsonNode.Parse(await propertyResponse.Content.ReadAsStringAsync())!.AsObject());

        using var deleteThing = await admin.DeleteAsync($"/sta/v1.1/Things({thingId})");
        deleteThing.StatusCode.Should().Be(HttpStatusCode.Conflict, await deleteThing.Content.ReadAsStringAsync());
        using var deleteSensor = await admin.DeleteAsync($"/sta/v1.1/Sensors({sensorId})");
        deleteSensor.StatusCode.Should().Be(HttpStatusCode.Conflict, await deleteSensor.Content.ReadAsStringAsync());
        using var deleteProperty = await admin.DeleteAsync($"/sta/v1.1/ObservedProperties({propertyId})");
        deleteProperty.StatusCode.Should().Be(HttpStatusCode.Conflict, await deleteProperty.Content.ReadAsStringAsync());

        using var thingStillThere = await _fixture.Client.GetAsync($"/sta/v1.1/Things({thingId})");
        thingStillThere.StatusCode.Should().Be(HttpStatusCode.OK);
        using var sensorStillThere = await _fixture.Client.GetAsync($"/sta/v1.1/Sensors({sensorId})");
        sensorStillThere.StatusCode.Should().Be(HttpStatusCode.OK);
        using var propertyStillThere = await _fixture.Client.GetAsync($"/sta/v1.1/ObservedProperties({propertyId})");
        propertyStillThere.StatusCode.Should().Be(HttpStatusCode.OK);
        using var streamStillThere = await _fixture.Client.GetAsync($"/sta/v1.1/Datastreams({streamId})");
        streamStillThere.StatusCode.Should().Be(HttpStatusCode.OK);

        using var unreferenced = await admin.PostAsync("/sta/v1.1/Things", Json(new JsonObject
        {
            ["name"] = "Unreferenced review thing",
            ["description"] = "No datastream"
        }));
        var unreferencedId = Id(await CreatedAsync(unreferenced));
        using var deleteUnreferenced = await admin.DeleteAsync($"/sta/v1.1/Things({unreferencedId})");
        deleteUnreferenced.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missingThing = await _fixture.Client.GetAsync($"/sta/v1.1/Things({unreferencedId})");
        missingThing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_PageRead_CountsOnlyWhenRequested()
    {
        using var warmup = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=1000");
        warmup.StatusCode.Should().Be(HttpStatusCode.OK);
        var expected = JsonNode.Parse(await warmup.Content.ReadAsStringAsync())!["value"]!.AsArray().Count;
        expected.Should().BeGreaterThan(0);

        using var plain = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=1");
        plain.StatusCode.Should().Be(HttpStatusCode.OK);
        var plainBody = JsonNode.Parse(await plain.Content.ReadAsStringAsync())!.AsObject();
        plainBody["value"]!.AsArray().Should().ContainSingle();
        plainBody.ContainsKey("@iot.count").Should().BeFalse();

        using var disabled = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=1&$count=false");
        disabled.StatusCode.Should().Be(HttpStatusCode.OK);
        var disabledBody = JsonNode.Parse(await disabled.Content.ReadAsStringAsync())!.AsObject();
        disabledBody.ContainsKey("@iot.count").Should().BeFalse();

        using var counted = await _fixture.Client.GetAsync("/sta/v1.1/Things?$top=1&$count=true");
        counted.StatusCode.Should().Be(HttpStatusCode.OK);
        var countedBody = JsonNode.Parse(await counted.Content.ReadAsStringAsync())!.AsObject();
        countedBody["@iot.count"]!.GetValue<long>().Should().Be(expected);
        countedBody["value"]!.AsArray().Should().ContainSingle();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /sta/v1.1/Things")]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Observations({id})/FeatureOfInterest")]
    public async Task LocationlessObservation_ConformantProfileRejectsNullFeature_AndRelaxedModeKeepsIt()
    {
        var featureId = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        using var admin = _fixture.CreateAdminClient();
        using var createdThing = await admin.PostAsync("/sta/v1.1/Things", Json(new JsonObject
        {
            ["name"] = "Locationless review thing",
            ["description"] = "No location, so no inferred feature"
        }));
        var thingId = Id(await CreatedAsync(createdThing));
        using var createdStream = await admin.PostAsync("/sta/v1.1/Datastreams", Json(new JsonObject
        {
            ["name"] = "Locationless review stream",
            ["description"] = "Synthetic",
            ["unitOfMeasurement"] = new JsonObject { ["name"] = "Celsius", ["symbol"] = "C", ["definition"] = "https://example.test/celsius" },
            ["Thing"] = new JsonObject { ["@iot.id"] = thingId },
            ["Sensor"] = new JsonObject { ["@iot.id"] = 1 },
            ["ObservedProperty"] = new JsonObject { ["@iot.id"] = 1 }
        }));
        var streamId = Id(await CreatedAsync(createdStream));
        var featuresBefore = await ScalarAsync("SELECT count(*)::bigint FROM sta_feature_of_interest");
        var observationsBefore = await ScalarAsync(
            "SELECT count(*)::bigint FROM sta_observation WHERE datastream_id = @id",
            ("id", streamId));

        using var relaxed = await admin.PostAsync("/sta/v1.1/Observations", Json(new JsonObject
        {
            ["result"] = 1.25,
            ["Datastream"] = new JsonObject { ["@iot.id"] = streamId }
        }));
        var relaxedObservation = await CreatedAsync(relaxed);
        var relaxedId = Id(relaxedObservation);
        (await ScalarAsync("SELECT count(*)::bigint FROM sta_feature_of_interest")).Should().Be(featuresBefore);
        (await ScalarAsync(
            "SELECT count(*)::bigint FROM sta_observation WHERE datastream_id = @id",
            ("id", streamId))).Should().Be(observationsBefore + 1);
        (await ScalarAsync(
            "SELECT CASE WHEN feature_of_interest_reference_id IS NULL THEN 1 ELSE 0 END FROM sta_observation WHERE id = @id",
            ("id", relaxedId))).Should().Be(1, "a relaxed locationless insert stores a null feature");
        using var missingFeature = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({relaxedId})/FeatureOfInterest");
        missingFeature.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var configuration = _fixture.GetService<IConfiguration>();
        var previous = configuration["SensorThings:ConformantProfile"];
        try
        {
            configuration["SensorThings:ConformantProfile"] = "true";
            using var rejected = await admin.PostAsync("/sta/v1.1/Observations", Json(new JsonObject
            {
                ["result"] = 2.5,
                ["Datastream"] = new JsonObject { ["@iot.id"] = streamId }
            }));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, await rejected.Content.ReadAsStringAsync());
            (await ScalarAsync("SELECT count(*)::bigint FROM sta_feature_of_interest")).Should().Be(featuresBefore);
            (await ScalarAsync(
                "SELECT count(*)::bigint FROM sta_observation WHERE datastream_id = @id",
                ("id", streamId))).Should().Be(observationsBefore + 1);

            using var explicitFeature = await admin.PostAsync("/sta/v1.1/Observations", Json(new JsonObject
            {
                ["result"] = 3.5,
                ["Datastream"] = new JsonObject { ["@iot.id"] = streamId },
                ["FeatureOfInterest"] = new JsonObject { ["@iot.id"] = featureId }
            }));
            var explicitId = Id(await CreatedAsync(explicitFeature));
            (await ScalarAsync(
                "SELECT feature_of_interest_reference_id FROM sta_observation WHERE id = @id",
                ("id", explicitId))).Should().Be(featureId);
            (await ScalarAsync("SELECT count(*)::bigint FROM sta_feature_of_interest")).Should().Be(featuresBefore);
        }
        finally
        {
            configuration["SensorThings:ConformantProfile"] = previous;
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_NextLink_OmitsAccessToken()
    {
        using var response = await _fixture.Client.GetAsync("/sta/v1.1/Observations?access_token=secret&$top=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        body["value"]!.AsArray().Should().ContainSingle();
        var next = body["@iot.nextLink"]!.GetValue<string>();
        next.Should().NotContain("access_token");
        next.Should().NotContain("secret");
        var query = QueryHelpers.ParseQuery(new Uri(next).Query);
        query.ContainsKey("access_token").Should().BeFalse();
        query["$top"].ToString().Should().Be("1");
        query["$skip"].ToString().Should().Be("1");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/{**resourcePath}")]
    [Endpoint("GET /sta/v1.1/Things({id})/Datastreams")]
    public async Task Navigation_DepthAboveCap_ReturnsBadRequest_AndOneLevelStillSucceeds()
    {
        const string root = "/sta/v1.1/Things(1)/";
        var overCap = string.Join('/', Enumerable.Range(0, SensorThingsRelationships.MaxRelationshipDepth + 1)
            .Select(index => index % 2 == 0 ? "Locations(1)" : "Things(1)"));
        using var tooDeep = await _fixture.Client.GetAsync(root + overCap);
        tooDeep.StatusCode.Should().Be(HttpStatusCode.BadRequest, await tooDeep.Content.ReadAsStringAsync());

        var atCap = string.Join('/', Enumerable.Range(0, SensorThingsRelationships.MaxRelationshipDepth)
            .Select(index => index % 2 == 0 ? "Locations(1)" : "Things(1)"));
        using var missingLink = await _fixture.Client.GetAsync(root + atCap);
        missingLink.StatusCode.Should().Be(HttpStatusCode.NotFound, "a path at the cap is queried, and Locations(1) is absent");

        using var oneLevel = await _fixture.Client.GetAsync("/sta/v1.1/Things(1)/Datastreams");
        oneLevel.StatusCode.Should().Be(HttpStatusCode.OK, await oneLevel.Content.ReadAsStringAsync());
        JsonNode.Parse(await oneLevel.Content.ReadAsStringAsync())!["value"]!.AsArray().Should().NotBeEmpty();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Operation(Operations.Update)]
    [Endpoint("POST /sta/v1.1/Datastreams")]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task Observation_TimeAndInvalidResultPatch_RollsBack_AndValidTimePatchSucceeds(bool changeDatastream)
    {
        using var admin = _fixture.CreateAdminClient();
        var featureId = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        var streamId = 1L;
        if (changeDatastream)
        {
            using var createdStream = await admin.PostAsync("/sta/v1.1/Datastreams", Json(new JsonObject
            {
                ["name"] = "Truth patch target",
                ["description"] = "Validates the result after a partition-key change",
                ["observationType"] = "http://www.opengis.net/def/observationType/OGC-OM/2.0/OM_TruthObservation",
                ["unitOfMeasurement"] = new JsonObject { ["name"] = "Boolean", ["symbol"] = "bool", ["definition"] = "https://example.test/boolean" },
                ["Thing"] = new JsonObject { ["@iot.id"] = 1 },
                ["Sensor"] = new JsonObject { ["@iot.id"] = 1 },
                ["ObservedProperty"] = new JsonObject { ["@iot.id"] = 1 }
            }));
            streamId = Id(await CreatedAsync(createdStream));
        }
        const string originalTime = "2026-01-01T00:00:00Z";
        const string newTime = "2026-02-01T00:00:00Z/2026-02-02T00:00:00Z";
        using var created = await admin.PostAsync("/sta/v1.1/Observations", Json(new JsonObject
        {
            ["result"] = 4.5,
            ["phenomenonTime"] = originalTime,
            ["Datastream"] = new JsonObject { ["@iot.id"] = 1 },
            ["FeatureOfInterest"] = new JsonObject { ["@iot.id"] = featureId }
        }));
        var observationId = Id(await CreatedAsync(created));
        using var before = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({observationId})");
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        var original = await before.Content.ReadAsStringAsync();
        var invalid = new JsonObject { ["phenomenonTime"] = newTime };
        if (changeDatastream) invalid["Datastream"] = new JsonObject { ["@iot.id"] = streamId };
        else invalid["result"] = "not a measurement";
        using var rejected = await admin.PatchAsync($"/sta/v1.1/Observations({observationId})", Json(invalid));
        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest, await rejected.Content.ReadAsStringAsync());
        using var after = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({observationId})");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await after.Content.ReadAsStringAsync()).Should().Be(original, "the whole timestamp/result/binding mutation must roll back");
        (await ScalarAsync("SELECT count(*)::bigint FROM sta_observation WHERE id=@id AND phenomenon_time=@time AND result=4.5 AND datastream_id=1",
            ("id", observationId), ("time", DateTimeOffset.Parse(originalTime, CultureInfo.InvariantCulture)))).Should().Be(1);
        var valid = new JsonObject { ["phenomenonTime"] = newTime };
        if (changeDatastream)
        {
            valid["Datastream"] = new JsonObject { ["@iot.id"] = streamId };
            valid["result"] = true;
        }
        else valid["result"] = 9.5;
        using var accepted = await admin.PatchAsync($"/sta/v1.1/Observations({observationId})", Json(valid));
        accepted.StatusCode.Should().Be(HttpStatusCode.NoContent, await accepted.Content.ReadAsStringAsync());
        using var updated = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({observationId})");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = JsonNode.Parse(await updated.Content.ReadAsStringAsync())!.AsObject();
        var interval = body["phenomenonTime"]!.GetValue<string>().Split('/');
        interval.Should().HaveCount(2);
        DateTimeOffset.Parse(interval[0], CultureInfo.InvariantCulture).Should().Be(new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero));
        DateTimeOffset.Parse(interval[1], CultureInfo.InvariantCulture).Should().Be(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        if (changeDatastream) body["result"]!.GetValue<bool>().Should().BeTrue();
        else body["result"]!.GetValue<double>().Should().Be(9.5);
        (await ScalarAsync("SELECT count(*)::bigint FROM sta_observation WHERE id=@id AND phenomenon_time=@start AND phenomenon_time_end=@end AND datastream_id=@stream",
            ("id", observationId), ("start", new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            ("end", new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero)), ("stream", streamId))).Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /sta/v1.1/Observations({id})")]
    [Endpoint("DELETE /sta/v1.1/Observations({id})")]
    [Endpoint("POST /sta/v1.1/Observations")]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task Observations_DuplicateId_PatchAndDeleteConflictWithoutChangingRows()
    {
        await InsertDuplicateObservationAsync(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), 11);
        await InsertDuplicateObservationAsync(new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero), 22);
        using var admin = _fixture.CreateAdminClient();
        using var patch = await admin.PatchAsync(
            $"/sta/v1.1/Observations({DuplicateObservationId})",
            Json(new JsonObject { ["result"] = 99 }));
        patch.StatusCode.Should().Be(HttpStatusCode.Conflict, await patch.Content.ReadAsStringAsync());
        using var delete = await admin.DeleteAsync($"/sta/v1.1/Observations({DuplicateObservationId})");
        delete.StatusCode.Should().Be(HttpStatusCode.Conflict, await delete.Content.ReadAsStringAsync());
        (await ObservationResultsAsync(DuplicateObservationId)).Should().Equal(11d, 22d);

        using var missingPatch = await admin.PatchAsync(
            "/sta/v1.1/Observations(8800002)",
            Json(new JsonObject { ["result"] = 1 }));
        missingPatch.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var missingDelete = await admin.DeleteAsync("/sta/v1.1/Observations(8800002)");
        missingDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var featureId = await SensorThingsTestData.CreateFeatureAsync(_fixture);
        using var created = await admin.PostAsync("/sta/v1.1/Observations", Json(new JsonObject
        {
            ["result"] = 4.5,
            ["Datastream"] = new JsonObject { ["@iot.id"] = 1 },
            ["FeatureOfInterest"] = new JsonObject { ["@iot.id"] = featureId }
        }));
        var uniqueId = Id(await CreatedAsync(created));
        using var uniquePatch = await admin.PatchAsync(
            $"/sta/v1.1/Observations({uniqueId})",
            Json(new JsonObject { ["result"] = 9.5 }));
        uniquePatch.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var updated = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({uniqueId})");
        updated.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonNode.Parse(await updated.Content.ReadAsStringAsync())!["result"]!.GetValue<double>().Should().Be(9.5);
        using var uniqueDelete = await admin.DeleteAsync($"/sta/v1.1/Observations({uniqueId})");
        uniqueDelete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var missing = await _fixture.Client.GetAsync($"/sta/v1.1/Observations({uniqueId})");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ObservationResultsAsync(DuplicateObservationId)).Should().Equal(11d, 22d);
    }
}
