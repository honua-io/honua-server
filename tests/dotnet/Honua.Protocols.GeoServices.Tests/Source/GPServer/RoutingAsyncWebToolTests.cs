// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Geoprocessing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

/// <summary>Exercises routing GP aliases through the real durable worker and canonical provider.</summary>
[Collection("Redis")]
[Protocol(TestProtocols.GPServer)]
public sealed class RoutingAsyncWebToolTests(RedisFixture redis) : IAsyncLifetime
{
    private const string ServiceId = WebAppFixture.TestServiceId;
    private const string FirstPoint = """
        {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"fields":[{"name":"OBJECTID","type":"esriFieldTypeOID"},{"name":"Name","type":"esriFieldTypeString"}],"features":[{"geometry":{"x":-122,"y":37},"attributes":{"OBJECTID":0,"Name":"First"}}]}
        """;
    private const string SecondPoint = """
        {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"features":[{"geometry":{"x":-121,"y":38},"attributes":{"OBJECTID":17,"Name":"Second","Weight":3}}]}
        """;
    private const string RouteGeometry = """{"type":"LineString","coordinates":[[-122,37],[-121,38]]}""";

    public Task InitializeAsync() => DeleteControlPlaneKeysAsync();
    public Task DisposeAsync() => DeleteControlPlaneKeysAsync();

    private async Task DeleteControlPlaneKeysAsync()
    {
        await using var multiplexer = await ConnectionMultiplexer.ConnectAsync(GPServerRedisTestConnection.For(redis));
        var keys = multiplexer.GetServer(multiplexer.GetEndPoints()[0]).Keys(pattern: "controlplane:*").ToArray();
        if (keys.Length > 0)
        {
            await multiplexer.GetDatabase().KeyDeleteAsync(keys);
        }
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes", "IncidentID", "FacilityID", "GPServer")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines", "OriginID", "DestinationID", "GPServer")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines", "FacilityID", "DemandID", "GPServer")]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes", "IncidentID", "FacilityID", "NAServer")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines", "OriginID", "DestinationID", "NAServer")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines", "FacilityID", "DemandID", "NAServer")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task SubmitJob_StockFeatureSet_CompletesWithNamedResultsAndInputIdentity(
        string task, string first, string second, string resultName, string firstId, string secondId, string family)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        client.Timeout = GPServerJobWait.RequestTimeout;
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint }, family);
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId, family), jobId,
            fixture.GetService<IExecutionJobStore>());
        terminal.RootElement.GetProperty("results").GetProperty(resultName).GetProperty("paramUrl").GetString()
            .Should().Be("results/" + resultName);
        using var result = await ResultAsync(client, task, jobId, resultName, family);
        var value = result.RootElement.GetProperty("value");
        value.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        var features = value.GetProperty("features");
        features.GetArrayLength().Should().Be(1);
        var attributes = features[0].GetProperty("attributes");
        attributes.GetProperty(firstId).GetInt64().Should().Be(0);
        attributes.GetProperty(secondId).GetInt64().Should().Be(17);
        attributes.GetProperty(task == "GenerateOriginDestinationCostMatrix" ? "Total_Time" : "Total_Minutes").GetDouble().Should().Be(2);
        using var succeeded = await ResultAsync(client, task, jobId, "Solve_Succeeded", family);
        succeeded.RootElement.GetProperty("dataType").GetString().Should().Be("GPBoolean");
        succeeded.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();
        var echoName = task == "FindClosestFacilities" ? "Output_Incidents" : task == "SolveLocationAllocation" ? "Output_Facilities" : "Output_Origins";
        using var echo = await ResultAsync(client, task, jobId, echoName, family);
        var echoed = echo.RootElement.GetProperty("value").GetProperty("features")[0].GetProperty("attributes");
        echoed.GetProperty("OBJECTID").GetInt64().Should().Be(0);
        echoed.GetProperty("Name").GetString().Should().Be("First");
        if (task == "GenerateOriginDestinationCostMatrix")
        {
            var call = provider.ReceivedCalls().Single(call => call.GetMethodInfo().Name == nameof(IRoutingProvider.SolveOdCostMatrixAsync));
            ((OdCostMatrixSolveRequest)call.GetArguments()[0]!).OutputType.Should().Be(OdLineOutputType.NoLines);
        }
        if (task == "SolveLocationAllocation")
        {
            echoed.GetProperty("FacilityType").GetInt32().Should().Be(3);
            attributes.GetProperty("Weight").GetDouble().Should().Be(3);
            attributes.GetProperty("TotalWeighted_Minutes").GetDouble().Should().Be(6);
            attributes.GetProperty("Name").GetString().Should().Be("First - Second");
            var path = features[0].GetProperty("geometry").GetProperty("paths")[0];
            path[0][0].GetDouble().Should().Be(-122);
            path[1][0].GetDouble().Should().Be(-121);
            echoed.GetProperty("DemandWeight").GetDouble().Should().Be(3);
        }
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Output_Routes", "Measurement_Units")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Output_Origin_Destination_Lines", "Time_Units")]
    [InlineData("SolveLocationAllocation", "Facilities", "Output_Allocation_Lines", "Measurement_Units")]
    [Operation(Operations.Get)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{layerName}")]
    public async Task TaskInfo_PublishesAsyncPointSchemaAndDocumentedOutputs(string task, string points, string output, string units)
    {
        await using var fixture = Fixture(Provider());
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var response = await client.GetAsync($"/rest/services/{ServiceId}/GPServer/{task}?f=json");
        using var alias = await client.GetAsync($"/rest/services/{ServiceId}/NAServer/{task}?f=json");
        alias.StatusCode.Should().Be(response.StatusCode);
        (await alias.Content.ReadAsStringAsync()).Should().Be(await response.Content.ReadAsStringAsync());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
        var parameters = body.RootElement.GetProperty("parameters").EnumerateArray().ToArray();
        var input = parameters.Single(parameter => parameter.GetProperty("name").GetString() == points);
        input.GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
        input.GetProperty("defaultValue").GetProperty("geometryType").GetString().Should().Be("esriGeometryPoint");
        input.GetProperty("defaultValue").GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        parameters.Single(parameter => parameter.GetProperty("name").GetString() == units)
            .GetProperty("defaultValue").GetString().Should().Be("Minutes");
        parameters.Single(parameter => parameter.GetProperty("name").GetString() == output)
            .GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
        parameters.Single(parameter => parameter.GetProperty("name").GetString() == "Solve_Succeeded")
            .GetProperty("dataType").GetString().Should().Be("GPBoolean");
        if (task == "SolveLocationAllocation")
        {
            parameters.Should().Contain(parameter => parameter.GetProperty("name").GetString() == "Default_Measurement_Cutoff");
            var direction = parameters.Single(parameter => parameter.GetProperty("name").GetString() == "Travel_Direction");
            direction.GetProperty("defaultValue").GetString().Should().Be("Facility to Demand");
            direction.GetProperty("choiceList").EnumerateArray().Select(value => value.GetString()).Should().Equal("Facility to Demand");
        }
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_NullAndZeroStockLocationDefaults_UseCanonicalSolve(string task, string first, string second)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var defaults = FirstPoint.Replace("\"Name\":\"First\"", "\"Name\":\"First\",\"FacilityType\":0,\"AdditionalTime\":null,\"AdditionalDistance\":0,\"AdditionalCost\":null,\"CurbApproach\":null,\"Cutoff\":0,\"Capacity\":null,\"TargetFacilityCount\":null,\"TargetDestinationCount\":null", StringComparison.Ordinal);
        var jobId = await SubmitAsync(client, task, new() { [first] = defaults, [second] = SecondPoint });
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        provider.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal)).Should().Be(1);
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_AdvertisedTravelModeJson_ReachesCanonicalProviderAsName(string task, string first, string second)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        const string mode = """{"name":"driving","id":"stable-profile-id","type":"CAR","impedanceAttributeName":"TravelTime","timeAttributeName":"TravelTime","distanceAttributeName":"Kilometers","restrictionAttributeNames":[],"attributeParameterValues":[],"useHierarchy":false,"uturnAtJunctions":"esriNFSBAllowBacktrack","simplificationTolerance":10,"simplificationToleranceUnits":"esriMeters"}""";
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint, ["Travel_Mode"] = mode });
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        var request = provider.ReceivedCalls().Single(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal)).GetArguments()[0];
        var actual = request switch { ClosestFacilitySolveRequest cf => cf.TravelMode,
            OdCostMatrixSolveRequest od => od.TravelMode, LocationAllocationSolveRequest la => la.TravelMode, _ => null };
        actual.Should().Be("driving");
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "{\"name\":\"driving\",\"impedanceAttributeName\":\"Kilometers\"}")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "{\"name\":\"driving\",\"useHierarchy\":true}")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "{\"name\":\"driving\",\"restrictionAttributeNames\":[\"Avoid Toll Roads\"]}")]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "{\"name\":\"unknown\"}")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "{\"name\":\"driving\",\"attributeParameterValues\":[{}]}")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "{\"name\":\"driving\",\"unknownSetting\":true}")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnsupportedTravelModeSettings_FailWithoutSolverOrArtifacts(string task, string first, string second, string mode)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint, ["Travel_Mode"] = mode });
        using var terminal = await GPServerJobWait.UntilRestTerminalAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobFailed");
        provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal));
        (await fixture.GetService<IExecutionJobStore>().GetAsync(jobId))!.ArtifactReferences.Should().BeEmpty();
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ClosestFacilityReverseDirection_PreservesProviderDirectionAndRouteName()
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, "FindClosestFacilities", new() { ["Incidents"] = FirstPoint,
            ["Facilities"] = SecondPoint, ["Travel_Direction"] = "Facility to Incident" });
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath("FindClosestFacilities", jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        await provider.Received(1).SolveClosestFacilityAsync(Arg.Is<ClosestFacilitySolveRequest>(request => request.Direction == ClosestFacilityTravelDirection.FromFacility), Arg.Any<CancellationToken>());
        using var output = await ResultAsync(client, "FindClosestFacilities", jobId, "Output_Routes");
        output.RootElement.GetProperty("value").GetProperty("features")[0].GetProperty("attributes")
            .GetProperty("Name").GetString().Should().Be("Second - First");
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes", "Measurement_Units")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines", "Time_Units")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines", "Measurement_Units")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_TimeUnits_ConvertsCutoffAndResultsWithoutChangingCoordinates(
        string task, string first, string second, string output, string units)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var inputs = new Dictionary<string, string> { [first] = FirstPoint, [second] = SecondPoint,
            [units] = "Seconds", [task == "SolveLocationAllocation" ? "Default_Measurement_Cutoff" : "Cutoff"] = "120" };
        if (task != "SolveLocationAllocation")
        {
            inputs["Distance_Units"] = "Kilometers";
        }
        var jobId = await SubmitAsync(client, task, inputs);
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        var request = provider.ReceivedCalls().Single(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal)).GetArguments()[0];
        var cutoff = request switch { ClosestFacilitySolveRequest cf => cf.Cutoff,
            OdCostMatrixSolveRequest od => od.Cutoff, LocationAllocationSolveRequest la => la.ImpedanceCutoff, _ => null };
        cutoff.Should().Be(2);
        using var result = await ResultAsync(client, task, jobId, output);
        var attributes = result.RootElement.GetProperty("value").GetProperty("features")[0].GetProperty("attributes");
        attributes.GetProperty(task == "GenerateOriginDestinationCostMatrix" ? "Total_Time" : "Total_Seconds").GetDouble().Should().Be(120);
        if (task != "SolveLocationAllocation")
        {
            attributes.GetProperty(task == "GenerateOriginDestinationCostMatrix" ? "Total_Distance" : "Total_Kilometers").GetDouble().Should().Be(1.609344);
        }
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnsupportedProvider_FailsDurablyWithoutPublishingResults(string task, string first, string second, string output)
    {
        var provider = Provider();
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities());
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint });
        using var terminal = await GPServerJobWait.UntilRestTerminalAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobFailed");
        provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal));
        var job = await fixture.GetService<IExecutionJobStore>().GetAsync(jobId);
        job!.ArtifactReferences.Should().BeEmpty();
        using var result = await client.GetAsync($"/rest/services/{ServiceId}/GPServer/{task}/jobs/{jobId}/results/{output}?f=json");
        using var error = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        error.RootElement.TryGetProperty("error", out _).Should().BeTrue();
    }

    [IntegrationTheory]
    [InlineData("Demand to Facility")]
    [InlineData("Not a direction")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_UnsupportedLocationAllocationDirection_RejectsBeforeProvider(string direction)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["f"] = "json", ["Facilities"] = FirstPoint,
            ["Demand_Points"] = SecondPoint, ["Travel_Direction"] = direction });
        using var response = await client.PostAsync($"/rest/services/{ServiceId}/GPServer/SolveLocationAllocation/submitJob", content);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal));
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_EmptyCanonicalSolve_ReturnsFalseAndEmptyResult(string task, string first, string second, string output)
    {
        var provider = Provider();
        provider.SolveClosestFacilityAsync(Arg.Any<ClosestFacilitySolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClosestFacilitySolveResult([]));
        provider.SolveOdCostMatrixAsync(Arg.Any<OdCostMatrixSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OdCostMatrixSolveResult([]));
        provider.SolveLocationAllocationAsync(Arg.Any<LocationAllocationSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocationAllocationSolveResult([], [], 0, 0));
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint });
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        using var result = await ResultAsync(client, task, jobId, output);
        result.RootElement.GetProperty("value").GetProperty("features").GetArrayLength().Should().Be(0);
        using var succeeded = await ResultAsync(client, task, jobId, "Solve_Succeeded");
        succeeded.RootElement.GetProperty("value").GetBoolean().Should().BeFalse();
        (await fixture.GetService<IExecutionJobStore>().GetAsync(jobId))!.ArtifactReferences.Should().HaveCount(4);
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ProviderException_FailsDurablyWithoutPartialResults(string task, string first, string second)
    {
        var provider = Provider();
        provider.SolveClosestFacilityAsync(Arg.Any<ClosestFacilitySolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("fixture routing failure"));
        provider.SolveOdCostMatrixAsync(Arg.Any<OdCostMatrixSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("fixture routing failure"));
        provider.SolveLocationAllocationAsync(Arg.Any<LocationAllocationSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("fixture routing failure"));
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, task, new() { [first] = FirstPoint, [second] = SecondPoint });
        using var terminal = await GPServerJobWait.UntilRestTerminalAsync(client, JobPath(task, jobId), jobId,
            fixture.GetService<IExecutionJobStore>());
        terminal.RootElement.GetProperty("jobStatus").GetString().Should().Be("esriJobFailed");
        provider.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal)).Should().Be(1);
        (await fixture.GetService<IExecutionJobStore>().GetAsync(jobId))!.ArtifactReferences.Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    public async Task SubmitJob_ConflictingInputSpatialReferences_RejectsBeforeSolver(string task, string first, string second)
    {
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json", [first] = FirstPoint,
            [second] = SecondPoint.Replace("4326", "3857", StringComparison.Ordinal),
        });
        using var response = await client.PostAsync($"/rest/services/{ServiceId}/GPServer/{task}/submitJob", content);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal));
    }

    [IntegrationTheory]
    [InlineData("GPServer")]
    [InlineData("NAServer")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{taskName}/submitJob")]
    public async Task SubmitJob_LocationAllocation_ReportsAssignedAndUnassignedDemandUsingOriginalObjectIds(string family)
    {
        const string demands = """
            {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":4326},"features":[{"geometry":{"x":-121,"y":38},"attributes":{"OBJECTID":0,"ID":100,"Weight":3}},{"geometry":{"x":-120,"y":39},"attributes":{"OBJECTID":17,"ID":200,"Weight":4}}]}
            """;
        var provider = Provider();
        provider.SolveLocationAllocationAsync(Arg.Any<LocationAllocationSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocationAllocationSolveResult([0], [new(0, 0, 3, 2), new(1, -1, 4, double.PositiveInfinity)], 6, 3));
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var jobId = await SubmitAsync(client, "SolveLocationAllocation", new()
        {
            ["Facilities"] = FirstPoint.Replace("\"OBJECTID\":0", "\"OBJECTID\":0,\"ID\":700", StringComparison.Ordinal),
            ["Demand_Points"] = demands,
        }, family);
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath("SolveLocationAllocation", jobId, family), jobId,
            fixture.GetService<IExecutionJobStore>());
        using var result = await ResultAsync(client, "SolveLocationAllocation", jobId, "Output_Demand_Points", family);
        var points = result.RootElement.GetProperty("value").GetProperty("features");
        points.GetArrayLength().Should().Be(2);
        var assigned = points[0].GetProperty("attributes");
        assigned.GetProperty("ID").GetInt64().Should().Be(100);
        assigned.GetProperty("DemandOID").GetInt64().Should().Be(0);
        assigned.GetProperty("FacilityOID").GetInt64().Should().Be(0);
        assigned.GetProperty("AllocatedWeight").GetDouble().Should().Be(3);
        var unassigned = points[1].GetProperty("attributes");
        unassigned.GetProperty("ID").GetInt64().Should().Be(200);
        unassigned.GetProperty("DemandOID").GetInt64().Should().Be(17);
        unassigned.GetProperty("FacilityOID").ValueKind.Should().Be(JsonValueKind.Null);
        unassigned.GetProperty("AllocatedWeight").ValueKind.Should().Be(JsonValueKind.Null);
        assigned.TryGetProperty("Status", out _).Should().BeFalse();
        unassigned.TryGetProperty("Status", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData("GPServer", false, "4326")]
    [InlineData("NAServer", false, "4326")]
    [InlineData("GPServer", true, "4326")]
    [InlineData("NAServer", true, "4326")]
    [InlineData("GPServer", false, "[4326]")]
    [InlineData("NAServer", false, "[4326]")]
    [Operation(Operations.Create)]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{taskName}/submitJob")]
    public async Task SubmitJob_MalformedFeatureSpatialReference_ReturnsValidationErrorBeforeSolver(string family, bool geometryReference, string reference)
    {
        var points = geometryReference
            ? FirstPoint.Replace("\"x\":-122", "\"spatialReference\":" + reference + ",\"x\":-122", StringComparison.Ordinal)
            : FirstPoint.Replace("{\"wkid\":4326}", reference, StringComparison.Ordinal);
        var provider = Provider();
        await using var fixture = Fixture(provider);
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["f"] = "json", ["Incidents"] = points, ["Facilities"] = SecondPoint,
        });
        using var response = await client.PostAsync($"/rest/services/{ServiceId}/{family}/FindClosestFacilities/submitJob", content);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        provider.ReceivedCalls().Should().NotContain(call => call.GetMethodInfo().Name.StartsWith("Solve", StringComparison.Ordinal));
    }

    [IntegrationTheory]
    [InlineData("FindClosestFacilities", "Incidents", "Facilities", "Output_Routes")]
    [InlineData("GenerateOriginDestinationCostMatrix", "Origins", "Destinations", "Output_Origin_Destination_Lines")]
    [InlineData("SolveLocationAllocation", "Facilities", "Demand_Points", "Output_Allocation_Lines")]
    [Operation(Operations.Create)]
    [Endpoint("GET /rest/services/{serviceId}/NAServer/{taskName}/submitJob")]
    public async Task SubmitJob_NaServerGetAlias_UsesTheSameDurableJobAndResults(string task, string first, string second, string output)
    {
        await using var fixture = Fixture(Provider());
        await fixture.InitializeAsync();
        using var client = fixture.CreateAdminClient();
        var query = $"f=json&{first}={Uri.EscapeDataString(FirstPoint)}&{second}={Uri.EscapeDataString(SecondPoint)}";
        using var response = await client.GetAsync($"/rest/services/{ServiceId}/NAServer/{task}/submitJob?{query}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var submitted = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var jobId = submitted.RootElement.GetProperty("jobId").GetString()!;
        using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client, JobPath(task, jobId, "NAServer"), jobId,
            fixture.GetService<IExecutionJobStore>());
        using var alias = await ResultAsync(client, task, jobId, output, "NAServer");
        using var canonical = await ResultAsync(client, task, jobId, output);
        alias.RootElement.GetRawText().Should().Be(canonical.RootElement.GetRawText());
        (await fixture.GetService<IExecutionJobStore>().GetAsync(jobId))!.ArtifactReferences.Should().HaveCount(4);
    }

    private static IRoutingProvider Provider()
    {
        var provider = Substitute.For<IRoutingProvider>();
        provider.Name.Returns("fixture");
        provider.GetCapabilitiesAsync(Arg.Any<CancellationToken>()).Returns(new RoutingProviderCapabilities(
            SupportsClosestFacility: true, SupportsOdCostMatrix: true, SupportsLocationAllocation: true)
        { SupportsOdStraightLines = true, SupportedTravelModes = ["driving"], SupportedLocationAllocationProblemTypes = [LocationAllocationProblemType.MinimizeImpedance,
            LocationAllocationProblemType.MaximizeCoverage, LocationAllocationProblemType.MinimizeFacilities] });
        provider.SolveClosestFacilityAsync(Arg.Any<ClosestFacilitySolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ClosestFacilitySolveResult([new(0, 0, 1, RouteGeometry, 1609.344, 2, [])]));
        provider.SolveOdCostMatrixAsync(Arg.Any<OdCostMatrixSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OdCostMatrixSolveResult([new(0, 0, 1, 2, 1609.344)]));
        provider.SolveLocationAllocationAsync(Arg.Any<LocationAllocationSolveRequest>(), Arg.Any<CancellationToken>())
            .Returns(new LocationAllocationSolveResult([0], [new(0, 0, 3, 2)], 6, 3));
        return provider;
    }

    private WebAppFixture Fixture(IRoutingProvider provider) => new WebAppFixture()
        .ConfigureWebHost(builder => builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:redis"] = GPServerRedisTestConnection.For(redis) })))
        .ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddSingleton(provider);
            services.RemoveAll<IConnectionMultiplexer>();
            services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(GPServerRedisTestConnection.For(redis)));
            services.RemoveAll<IExecutionJobStore>();
            services.AddSingleton<IExecutionJobStore>(sp => new RedisExecutionJobStore(sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionJobStore>>()));
            services.RemoveAll<IGeoprocessingResultPackageStore>();
            services.AddSingleton<IGeoprocessingResultPackageStore>(sp => new RedisGeoprocessingResultPackageStore(
                sp.GetRequiredService<IConnectionMultiplexer>(), sp.GetRequiredService<IOptionsMonitor<GeoprocessingExecutorOptions>>(),
                sp.GetRequiredService<ILogger<RedisGeoprocessingResultPackageStore>>()));
            services.RemoveAll<RedisJobQueue>();
            services.RemoveAll<IJobQueue>();
            services.RemoveAll<IQueueClaimReconciler>();
            services.AddSingleton<RedisJobQueue>(sp => new RedisJobQueue(sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<IExecutionJobStore>(), sp.GetRequiredService<ILogger<RedisJobQueue>>()));
            services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<RedisJobQueue>());
            services.AddSingleton<IQueueClaimReconciler>(sp => sp.GetRequiredService<RedisJobQueue>());
            services.RemoveAll<IExecutionLogStore>();
            services.AddSingleton<IExecutionLogStore>(sp => new RedisExecutionLogStore(sp.GetRequiredService<IConnectionMultiplexer>(),
                sp.GetRequiredService<ILogger<RedisExecutionLogStore>>()));
            services.AddJobWorker();
        });

    private static string JobPath(string task, string jobId, string family = "GPServer") => $"/rest/services/{ServiceId}/{family}/{task}/jobs/{jobId}?f=json";

    private static async Task<string> SubmitAsync(HttpClient client, string task, Dictionary<string, string> inputs, string family = "GPServer")
    {
        inputs["f"] = "json";
        using var content = new FormUrlEncodedContent(inputs);
        using var response = await client.PostAsync($"/rest/services/{ServiceId}/{family}/{task}/submitJob", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var body = JsonDocument.Parse(text);
        return body.RootElement.GetProperty("jobId").GetString()!;
    }

    private static async Task<JsonDocument> ResultAsync(HttpClient client, string task, string jobId, string output, string family = "GPServer")
    {
        using var response = await client.GetAsync($"/rest/services/{ServiceId}/{family}/{task}/jobs/{jobId}/results/{output}?f=json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}
