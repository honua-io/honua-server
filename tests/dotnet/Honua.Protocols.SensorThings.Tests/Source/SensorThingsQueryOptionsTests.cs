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
/// System query-option conformance for the STA read surface (#4201, #4203). Before these
/// fixes <c>$filter</c> on the catalog entity sets, the <c>$orderby</c> property,
/// <c>$select</c> and <c>$expand</c> were parsed and then dropped: every request answered
/// 200 with unfiltered, unsorted, complete entities, so a client could not tell that the
/// option had been ignored. A <c>$filter</c> literal of the wrong type reached PostgreSQL
/// untyped and came back as a 500.
///
/// The expected values here are computed from the seeded series
/// (<c>result = 15 + 10 * sin(id)</c> over ids 1..48, hourly from 2026-01-01T01:00Z), not
/// snapshotted from the server's own output.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsQueryOptionsTests : IAsyncLifetime
{
    private const int SeededObservationCount = 48;

    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>The seeded result for observation <paramref name="id"/>, computed here.</summary>
    private static double SeededResult(int id) => 15.0d + (10.0d * Math.Sin(id));

    private static IEnumerable<int> SeededIds => Enumerable.Range(1, SeededObservationCount);

    private async Task<JsonDocument> GetOkAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "GET {0} must succeed", path);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<HttpStatusCode> GetStatusAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        return response.StatusCode;
    }

    private static long[] Ids(JsonDocument document) =>
        document.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("@iot.id").GetInt64()).ToArray();

    private static string Escape(string value) => Uri.EscapeDataString(value);

    // ---- $orderby ----

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_OrderByResultDescending_ReturnsTheLargestResultsNotTheNewestRows()
    {
        // Independently computed: the three largest values of 15 + 10*sin(id).
        var expected = SeededIds
            .OrderByDescending(SeededResult)
            .ThenBy(id => id)
            .Take(3)
            .Select(id => (long)id)
            .ToArray();

        using var document = await GetOkAsync($"/sta/v1.1/Observations?$orderby={Escape("result desc")}&$top=3");

        Ids(document).Should().Equal(expected,
            "only the trailing ' desc' token used to be honoured, so the rows came back in phenomenon-time order");
        var results = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("result").GetDouble()).ToArray();
        results.Should().BeInDescendingOrder();
        results[0].Should().BeApproximately(SeededResult((int)expected[0]), 1e-9);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_OrderByResultAscending_ReturnsTheSmallestResults()
    {
        var expected = SeededIds
            .OrderBy(SeededResult)
            .ThenBy(id => id)
            .Take(3)
            .Select(id => (long)id)
            .ToArray();

        using var document = await GetOkAsync($"/sta/v1.1/Observations?$orderby=result&$top=3");

        Ids(document).Should().Equal(expected);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_OrderByUnknownProperty_Returns400()
    {
        (await GetStatusAsync($"/sta/v1.1/Observations?$orderby={Escape("notAProperty desc")}"))
            .Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync($"/sta/v1.1/Observations?$orderby={Escape("result sideways")}"))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task Datastreams_OrderByNameDescending_SortsByTheNamedProperty()
    {
        using var admin = _fixture.CreateAdminClient();
        foreach (var name in new[] { "AAA Stream", "ZZZ Stream" })
        {
            using var created = await admin.PostAsync("/sta/v1.1/Datastreams", Json($$"""
                {
                  "name": "{{name}}",
                  "description": "ordering fixture",
                  "unitOfMeasurement": { "name": "metre per second", "symbol": "m/s", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
                  "Thing": { "name": "{{name}} Station", "description": "x" },
                  "Sensor": { "name": "{{name}} Sensor", "description": "x" },
                  "ObservedProperty": { "name": "{{name}} Property", "description": "x" }
                }
                """));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }

        using var document = await GetOkAsync($"/sta/v1.1/Datastreams?$orderby={Escape("name desc")}&$top=1000");
        var names = document.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString()!).ToArray();

        names.Should().BeInDescendingOrder(StringComparer.Ordinal);
        names[0].Should().Be("ZZZ Stream");
        names[^1].Should().Be("AAA Stream");
    }

    // ---- $filter on the catalog entity sets ----

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_FilterOnName_ReturnsOnlyTheMatchingThing()
    {
        using var admin = _fixture.CreateAdminClient();
        using var created = await admin.PostAsync("/sta/v1.1/Datastreams", Json("""
            {
              "name": "Filterable Datastream",
              "description": "filter fixture",
              "unitOfMeasurement": { "name": "metre per second", "symbol": "m/s", "definition": "http://unitsofmeasure.org/ucum.html#para-30" },
              "Thing": { "name": "Filterable Station", "description": "filter fixture" },
              "Sensor": { "name": "Filterable Sensor", "description": "filter fixture" },
              "ObservedProperty": { "name": "Filterable Property", "description": "filter fixture" }
            }
            """));
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        using var matching = await GetOkAsync($"/sta/v1.1/Things?$filter={Escape("name eq 'Filterable Station'")}&$count=true");
        matching.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(1);
        matching.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString()).Should().Equal("Filterable Station");

        // The repro from the issue: a filter matching nothing used to return every Thing.
        using var empty = await GetOkAsync($"/sta/v1.1/Things?$filter={Escape("name eq 'nope'")}&$count=true");
        empty.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
        empty.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Sensors")]
    public async Task Sensors_FilterOnId_ReturnsOnlyTheMatchingSensor()
    {
        using var document = await GetOkAsync($"/sta/v1.1/Sensors?$filter={Escape("id eq 1")}&$count=true");

        Ids(document).Should().Equal(1L);
        document.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(1);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/ObservedProperties")]
    public async Task ObservedProperties_FilterThatMatchesNothing_ReturnsAnEmptyCollection()
    {
        using var document = await GetOkAsync(
            $"/sta/v1.1/ObservedProperties?$filter={Escape("description eq 'not a description in this catalog'")}&$count=true");

        document.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
        document.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_FilterOnResult_ReturnsExactlyTheComputedMatches()
    {
        // 15 + 10*sin(id) > 20  <=>  sin(id) > 0.5
        var expected = SeededIds.Where(id => SeededResult(id) > 20.0d).Select(id => (long)id).ToArray();
        expected.Should().NotBeEmpty("the seeded series must contain matches for the oracle to mean anything");

        using var document = await GetOkAsync($"/sta/v1.1/Observations?$filter={Escape("result gt 20")}&$top=1000&$count=true");

        Ids(document).OrderBy(id => id).Should().Equal(expected.OrderBy(id => id));
        document.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(expected.Length);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_FilterOnUnknownProperty_Returns400()
    {
        (await GetStatusAsync($"/sta/v1.1/Things?$filter={Escape("resultTime gt 2026-01-01T00:00:00Z")}"))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- $filter literal typing (#4203) ----

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_FilterWithAStringLiteralAgainstANumericProperty_Returns400NotAnUnhandled500()
    {
        // PostgreSQL has no implicit text -> double precision cast: this used to reach the
        // reader as an untyped text parameter and raise 42883 past the handler.
        (await GetStatusAsync($"/sta/v1.1/Observations?$filter={Escape("result eq 'abc'")}"))
            .Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync($"/sta/v1.1/Observations?$filter={Escape("result eq true")}"))
            .Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync($"/sta/v1.1/Observations?$filter={Escape("id eq 'abc'")}"))
            .Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync($"/sta/v1.1/Observations?$filter={Escape("phenomenonTime gt 'not-a-time'")}"))
            .Should().Be(HttpStatusCode.BadRequest);
        (await GetStatusAsync($"/sta/v1.1/Things?$filter={Escape("name eq 42")}"))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_FilterWithAWellTypedLiteral_StillMatches()
    {
        using var byId = await GetOkAsync($"/sta/v1.1/Observations?$filter={Escape("id eq 7")}");
        Ids(byId).Should().Equal(7L);

        using var byTime = await GetOkAsync(
            $"/sta/v1.1/Observations?$filter={Escape("phenomenonTime gt 2026-01-01T02:30:00Z and phenomenonTime lt 2026-01-01T04:30:00Z")}&$top=1000");
        // Seeded phenomenon times are 2026-01-01T00:00Z + id hours, so 03:00 and 04:00 match.
        Ids(byTime).OrderBy(id => id).Should().Equal(3L, 4L);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_FilterOnNull_UsesIsNullRatherThanAComparisonThatMatchesNothing()
    {
        using var admin = _fixture.CreateAdminClient();
        // Ingested without resultTime, so its result_time column is NULL; every seeded row
        // has one.
        using var created = await admin.PostAsync(
            "/sta/v1.1/Observations",
            Json("""{ "phenomenonTime": "2026-08-01T00:00:00Z", "result": 3.5, "Datastream": { "@iot.id": 1 } }"""));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var createdDocument = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var createdId = createdDocument.RootElement.GetProperty("@iot.id").GetInt64();

        using var isNull = await GetOkAsync($"/sta/v1.1/Observations?$filter={Escape("resultTime eq null")}&$top=1000&$count=true");
        Ids(isNull).Should().Equal([createdId],
            "`eq null` must become IS NULL, not `= NULL` which is never true");

        using var isNotNull = await GetOkAsync($"/sta/v1.1/Observations?$filter={Escape("resultTime ne null")}&$top=1000&$count=true");
        isNotNull.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(SeededObservationCount);

        // `ne <value>` expands to a null-safe shape built from the same null-test nodes:
        // the row with no resultTime must still be returned.
        using var notEqual = await GetOkAsync(
            $"/sta/v1.1/Observations?$filter={Escape("resultTime ne 2026-01-01T01:00:00Z")}&$top=1000&$count=true");
        Ids(notEqual).Should().Contain(createdId, "a NULL column is 'not equal' to a value under OData two-valued logic");
        notEqual.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(SeededObservationCount);

        // A relational comparison against null is constant-false (OData 4.01
        // §5.1.1.1.3-6). The shared parser folds it to a boolean literal, which the
        // translator emits as FALSE rather than failing the whole expression.
        using var relational = await GetOkAsync(
            $"/sta/v1.1/Observations?$filter={Escape("resultTime gt null and result gt 20")}&$count=true");
        relational.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(0);
    }

    // ---- $select ----

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_Select_ReturnsOnlyTheSelectedMembers()
    {
        using var document = await GetOkAsync($"/sta/v1.1/Observations?$select={Escape("@iot.id,result")}&$top=2");

        var entities = document.RootElement.GetProperty("value").EnumerateArray().ToArray();
        entities.Should().HaveCount(2);
        foreach (var entity in entities)
        {
            entity.EnumerateObject().Select(member => member.Name).Should()
                .BeEquivalentTo("@iot.id", "result");
        }

        // Envelope annotations are not entity properties and survive the projection.
        document.RootElement.TryGetProperty("value", out _).Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things({id})")]
    public async Task Things_SelectOnASingleEntity_ReturnsOnlyTheSelectedMembers()
    {
        using var document = await GetOkAsync($"/sta/v1.1/Things(1)?$select=name");

        document.RootElement.EnumerateObject().Select(member => member.Name).Should().BeEquivalentTo("name");
        document.RootElement.GetProperty("name").GetString().Should().Be("Demo Air Quality Station");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_SelectUnknownProperty_Returns400()
    {
        (await GetStatusAsync("/sta/v1.1/Things?$select=notAProperty")).Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- $expand ----

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastream_ExpandRelatedEntities_InlinesThingSensorAndObservedProperty()
    {
        using var document = await GetOkAsync($"/sta/v1.1/Datastreams(1)?$expand={Escape("Thing,Sensor,ObservedProperty")}");

        document.RootElement.GetProperty("Thing").GetProperty("name").GetString()
            .Should().Be("Demo Air Quality Station");
        document.RootElement.GetProperty("Sensor").GetProperty("name").GetString()
            .Should().Be("Demo Thermometer");
        document.RootElement.GetProperty("ObservedProperty").GetProperty("name").GetString()
            .Should().Be("Air Temperature");
        // The navigation links stay alongside the inline entities.
        document.RootElement.GetProperty("Thing@iot.navigationLink").GetString().Should().EndWith("/Datastreams(1)/Thing");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task Datastreams_WithoutExpand_DoNotCarryInlineRelatedEntities()
    {
        using var document = await GetOkAsync("/sta/v1.1/Datastreams");

        var datastream = document.RootElement.GetProperty("value").EnumerateArray().First();
        datastream.TryGetProperty("Thing", out _).Should().BeFalse();
        datastream.TryGetProperty("Observations", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastream_ExpandObservationsWithNestedOptions_HonoursNestedTopAndOrderby()
    {
        var expected = SeededIds
            .OrderByDescending(SeededResult)
            .ThenBy(id => id)
            .Take(3)
            .Select(id => (long)id)
            .ToArray();

        using var document = await GetOkAsync(
            $"/sta/v1.1/Datastreams(1)?$expand={Escape("Observations($top=3;$orderby=result desc)")}");

        document.RootElement.GetProperty("Observations").EnumerateArray()
            .Select(value => value.GetProperty("@iot.id").GetInt64())
            .Should().Equal(expected, "the nested options used to be stripped to the newest 100 observations");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastream_ExpandObservationsWithNestedFilter_HonoursTheNestedFilter()
    {
        var expected = SeededIds.Where(id => SeededResult(id) > 20.0d).Select(id => (long)id).OrderBy(id => id).ToArray();

        using var document = await GetOkAsync(
            $"/sta/v1.1/Datastreams(1)?$expand={Escape("Observations($filter=result gt 20;$top=1000)")}");

        document.RootElement.GetProperty("Observations").EnumerateArray()
            .Select(value => value.GetProperty("@iot.id").GetInt64())
            .OrderBy(id => id).Should().Equal(expected);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public async Task Things_ExpandDatastreams_Returns501RatherThanIgnoringTheOption()
    {
        (await GetStatusAsync("/sta/v1.1/Things?$expand=Datastreams"))
            .Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task Datastreams_ExpandUnknownNavigation_Returns400()
    {
        (await GetStatusAsync("/sta/v1.1/Datastreams?$expand=NotANavigation"))
            .Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})")]
    public async Task Datastream_ExpandWithAnUnsupportedNestedOption_Returns501()
    {
        (await GetStatusAsync($"/sta/v1.1/Datastreams(1)?$expand={Escape("Observations($select=result)")}"))
            .Should().Be(HttpStatusCode.NotImplemented);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations({id})")]
    public async Task Observation_ById_WithFilter_Returns400BecauseItCannotApply()
    {
        (await GetStatusAsync($"/sta/v1.1/Observations(1)?$filter={Escape("result gt 20")}"))
            .Should().Be(HttpStatusCode.BadRequest);
    }
}
