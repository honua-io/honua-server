// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>
/// The NetworkAnalysisUtilities tasks every GPServer publishes (#5035).
/// <c>arcpy.nax</c> refuses a stand-alone routing service dictionary without a
/// <c>utilityUrl</c>, and on that GP service it executes <c>GetTravelModes</c> then
/// <c>GetToolInfo</c> before it will solve; ArcGIS Pro 3.7.1 stopped at
/// "Task 'GetTravelModes' on service 'test_service' was not found".
/// </summary>
[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class NetworkAnalysisUtilitiesTaskTests : IClassFixture<NAServerEndpointTestsFixture>
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private readonly WebAppFixture _fixture;

    public NetworkAnalysisUtilitiesTaskTests(NAServerEndpointTestsFixture wrapper)
    {
        _fixture = wrapper.App;
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    public async Task ServiceInfo_ListsTheUtilityTasksNextToTheCatalogTasks()
    {
        using var response = await _fixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tasks = document.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.GetString()).ToArray();
        tasks.Should().Contain(["GetTravelModes", "GetToolInfo"]);
        tasks.Should().Contain("Buffer", "the catalog tasks are still published");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public async Task TaskInfo_DescribesGetTravelModesAsSynchronous()
    {
        using var response = await _fixture.Client.GetAsync($"/rest/services/{ServiceId}/GPServer/GetTravelModes?f=json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("name").GetString().Should().Be("GetTravelModes");
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        root.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("name").GetString())
            .Should().Equal("supportedTravelModes", "defaultTravelMode");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task GetTravelModes_ExecutesAnonymouslyAndReturnsTheEsriRecordSet()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        using var response = await _fixture.Client.PostAsync($"/rest/services/{ServiceId}/GPServer/GetTravelModes/execute", payload);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var results = document.RootElement.GetProperty("results").EnumerateArray().ToArray();
        results.Select(r => r.GetProperty("paramName").GetString()).Should().Equal("supportedTravelModes", "defaultTravelMode");

        var recordSet = results[0].GetProperty("value");
        results[0].GetProperty("dataType").GetString().Should().Be("GPRecordSet");
        recordSet.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString())
            .Should().Equal("ObjectID", "Name", "TravelModeId", "TravelMode", "AltName");
        var features = recordSet.GetProperty("features").EnumerateArray().ToArray();
        features.Should().NotBeEmpty();

        var defaultId = results[1].GetProperty("value").GetString();
        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty("TravelModeId").GetString().Should().Be(defaultId);
        using var mode = JsonDocument.Parse(attributes.GetProperty("TravelMode").GetString()!);
        mode.RootElement.GetProperty("id").GetString().Should().Be(defaultId);
        mode.RootElement.GetProperty("impedanceAttributeName").GetString().Should().Be("TravelTime");
        document.RootElement.GetProperty("messages").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task GetToolInfo_ReturnsTheNetworkDescriptionAndServiceLimits()
    {
        // The exact request arcpy.nax 3.7.1 issues before it will solve.
        using var response = await _fixture.Client.GetAsync(
            $"/rest/services/{ServiceId}/GPServer/GetToolInfo/execute?f=json&serviceName=asyncRoute&toolName=FindRoutes&includeNetworkSourceInfo=true");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("results")[0];
        result.GetProperty("paramName").GetString().Should().Be("toolInfo");
        result.GetProperty("dataType").GetString().Should().Be("GPString");

        // Esri embeds toolInfo as a JSON object, not an encoded string.
        var root = result.GetProperty("value");
        root.ValueKind.Should().Be(JsonValueKind.Object);
        root.GetProperty("networkDataset").GetProperty("networkAttributes").EnumerateArray()
            .Select(a => a.GetProperty("name").GetString()).Should().Contain("TravelTime");
        root.GetProperty("networkDataset").GetProperty("networkSources").EnumerateArray()
            .Select(s => s.GetProperty("elementType").GetString()).Should().Contain(["esriNETEdge", "esriNETJunction"]);
        root.GetProperty("serviceLimits").GetProperty("maximumStops").GetInt32().Should().BeGreaterThan(0);
        root.GetProperty("supportedTravelModes").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("defaultTravelMode").GetString().Should().HaveLength(16);
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task GetToolInfo_RejectsAToolNoSolverOwns()
    {
        using var payload = new FormUrlEncodedContent(
        [
            new KeyValuePair<string, string>("f", "json"),
            new KeyValuePair<string, string>("serviceName", "asyncVRP"),
            new KeyValuePair<string, string>("toolName", "SolveVehicleRoutingProblem"),
        ]);
        using var response = await _fixture.Client.PostAsync($"/rest/services/{ServiceId}/GPServer/GetToolInfo/execute", payload);

        // GeoServices REST errors travel as HTTP 200 envelopes carrying the Esri code.
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details")[0].GetString().Should().Contain("GetToolInfo");
    }

    [IntegrationTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task UtilityTask_HasNoJobForm()
    {
        using var payload = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        using var response = await _fixture.Client.PostAsync($"/rest/services/{ServiceId}/GPServer/GetTravelModes/submitJob", payload);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");
        error.GetProperty("code").GetInt32().Should().Be(400);
        error.GetProperty("details")[0].GetString().Should().Contain("synchronous");
    }
}
