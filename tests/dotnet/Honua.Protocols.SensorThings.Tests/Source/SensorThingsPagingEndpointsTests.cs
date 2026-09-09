// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.WebUtilities;

namespace Honua.Server.Tests.Features.Protocols.SensorThings;

/// <summary>HTTP regressions for QGIS count requests and SensorThings continuation links.</summary>
[Collection("Database")]
[Protocol(TestProtocols.SensorThings)]
public sealed class SensorThingsPagingEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<JsonDocument> ReadAsync(string path)
    {
        using var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task AssertZeroTopCountAsync(string path)
    {
        using var all = await ReadAsync(path + "?$top=1000");
        var expected = all.RootElement.GetProperty("value").GetArrayLength();
        expected.Should().BeInRange(1, 999, "the fixture must fit in the reference page");
        using var count = await ReadAsync(path + "?$top=0&$count=true");
        count.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
        count.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(expected);
        count.RootElement.TryGetProperty("@iot.nextLink", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    public Task Things_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/Things");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Sensors")]
    public Task Sensors_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/Sensors");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/ObservedProperties")]
    public Task ObservedProperties_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/ObservedProperties");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public Task Datastreams_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/Datastreams");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public Task Observations_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/Observations");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams({id})/Observations")]
    public Task DatastreamObservations_ZeroTop_ReturnsTotalCountWithoutContinuation() => AssertZeroTopCountAsync("/sta/v1.1/Datastreams(1)/Observations");

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_FilteredCount_IgnoresSkipAndTop()
    {
        var filter = Uri.EscapeDataString("result gt 20");
        using var all = await ReadAsync($"/sta/v1.1/Observations?$filter={filter}&$top=1000");
        var total = all.RootElement.GetProperty("value").GetArrayLength();
        total.Should().BeInRange(4, 999);
        using var page = await ReadAsync($"/sta/v1.1/Observations?$filter={filter}&$skip=2&$top=2&$count=true");
        page.RootElement.GetProperty("value").GetArrayLength().Should().Be(2);
        page.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(total);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_NextLink_PreservesQueryAndReturnsTheNextFilteredPage()
    {
        var filter = Uri.EscapeDataString("result gt 20");
        var order = Uri.EscapeDataString("phenomenonTime desc");
        var select = Uri.EscapeDataString("@iot.id,result,phenomenonTime");
        using var all = await ReadAsync($"/sta/v1.1/Observations?$filter={filter}&$orderby={order}&$top=1000");
        var expectedIds = all.RootElement.GetProperty("value").EnumerateArray()
            .Skip(2).Take(2).Select(value => value.GetProperty("@iot.id").GetInt64()).ToArray();
        expectedIds.Should().HaveCount(2);
        using var first = await ReadAsync($"/sta/v1.1/Observations?$filter={filter}&$orderby={order}&$select={select}&$top=2&$count=true");
        var next = first.RootElement.GetProperty("@iot.nextLink").GetString()!;
        var query = QueryHelpers.ParseQuery(new Uri(next).Query);
        query["$filter"].ToString().Should().Be("result gt 20");
        query["$orderby"].ToString().Should().Be("phenomenonTime desc");
        query["$select"].ToString().Should().Be("@iot.id,result,phenomenonTime");
        query["$count"].ToString().Should().Be("true");
        query["$skip"].ToString().Should().Be("2");
        using var second = await ReadAsync(next);
        second.RootElement.GetProperty("value").EnumerateArray()
            .Select(value => value.GetProperty("@iot.id").GetInt64()).Should().Equal(expectedIds);
        second.RootElement.GetProperty("@iot.count").GetInt64().Should()
            .Be(all.RootElement.GetProperty("value").GetArrayLength());
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    public async Task Datastreams_NextLink_RetainsObservationExpansion()
    {
        using var admin = _fixture.CreateAdminClient();
        using var content = new StringContent("""
            {"name":"Paging control stream","description":"Paging regression fixture",
             "unitOfMeasurement":{"name":"temperature","symbol":"C","definition":"https://example.test/unit"},
             "Thing":{"@iot.id":1},"Sensor":{"@iot.id":1},"ObservedProperty":{"@iot.id":1}}
            """, Encoding.UTF8, "application/json");
        using var created = await admin.PostAsync("/sta/v1.1/Datastreams", content);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        using var first = await ReadAsync("/sta/v1.1/Datastreams?$top=1&$expand=Observations&$count=true");
        var next = first.RootElement.GetProperty("@iot.nextLink").GetString()!;
        var query = QueryHelpers.ParseQuery(new Uri(next).Query);
        query["$expand"].ToString().Should().Be("Observations");
        query["$count"].ToString().Should().Be("true");
        using var second = await ReadAsync(next);
        var values = second.RootElement.GetProperty("value").EnumerateArray().ToArray();
        values.Should().HaveCount(1);
        values[0].GetProperty("Observations").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Things")]
    [Endpoint("GET /sta/v1.1/Sensors")]
    [Endpoint("GET /sta/v1.1/ObservedProperties")]
    [Endpoint("GET /sta/v1.1/Datastreams")]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task EntitySets_ExactLastPage_HasNoContinuation()
    {
        foreach (var entity in new[] { "Things", "Sensors", "ObservedProperties", "Datastreams", "Observations" })
        {
            using var all = await ReadAsync($"/sta/v1.1/{entity}?$top=1000");
            var total = all.RootElement.GetProperty("value").GetArrayLength();
            total.Should().BeInRange(1, 999);
            using var page = await ReadAsync($"/sta/v1.1/{entity}?$top={total}");
            page.RootElement.GetProperty("value").GetArrayLength().Should().Be(total);
            page.RootElement.TryGetProperty("@iot.nextLink", out _).Should().BeFalse();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_ZeroTopWithoutCount_HasNeitherCountNorContinuation()
    {
        using var page = await ReadAsync("/sta/v1.1/Observations?$top=0&$count=false");
        page.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
        page.RootElement.TryGetProperty("@iot.count", out _).Should().BeFalse();
        page.RootElement.TryGetProperty("@iot.nextLink", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /sta/v1.1/Observations")]
    public async Task Observations_EmptyFilteredResult_HasZeroCountAndNoContinuation()
    {
        var filter = Uri.EscapeDataString("result gt 1000000000");
        using var page = await ReadAsync($"/sta/v1.1/Observations?$filter={filter}&$top=2&$count=true");
        page.RootElement.GetProperty("value").GetArrayLength().Should().Be(0);
        page.RootElement.GetProperty("@iot.count").GetInt64().Should().Be(0);
        page.RootElement.TryGetProperty("@iot.nextLink", out _).Should().BeFalse();
    }
}
