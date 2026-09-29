// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed partial class GPServerDurableRuntimeTests
{
    [IntegrationTheory]
    [InlineData("FindRoutes", "Output_Routes", "Route", "routes")]
    [InlineData("GenerateServiceAreas", "Service_Areas", "ServiceArea", "saPolygons")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/submitJob")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    public async Task RoutingTools_DurableJobMatchesNAServerAndPublishesSoapContract(string task, string output, string solver, string oracleOutput)
    {
        await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        var fixture = CreateDurableFixture(productionExecutor: true)
            .ConfigureWebHost(builder => builder.UseSetting("Routing:Provider", "mock"));
        await fixture.InitializeAsync();
        try
        {
            using var client = fixture.CreateAdminClient();
            client.Timeout = GPServerJobWait.RequestTimeout;
            var taskUrl = $"/rest/services/{ServiceId}/GPServer/{task}";
            using var metadataResponse = await client.GetAsync(taskUrl + "?f=json");
            using var metadata = JsonDocument.Parse(await metadataResponse.Content.ReadAsStringAsync());
            metadata.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeAsynchronous");
            var parameterNames = metadata.RootElement.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("name").GetString()).ToArray();
            parameterNames.Should().Contain(output).And.Contain("Solve_Succeeded");

            using var soapBody = new StringContent("""
                <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
                  <soap:Body><GetToolInfos xmlns="http://www.esri.com/schemas/ArcGIS/10.8" /></soap:Body>
                </soap:Envelope>
                """, Encoding.UTF8, "text/xml");
            using var soap = await client.PostAsync($"/services/{ServiceId}/GPServer", soapBody);
            soap.StatusCode.Should().Be(HttpStatusCode.OK);
            var tool = XDocument.Parse(await soap.Content.ReadAsStringAsync()).Descendants("GPToolInfo")
                .Single(item => item.Element("Name")!.Value == task);
            tool.Element("ParameterInfo")!.Elements("GPParameterInfo").Select(item => item.Element("Name")!.Value)
                .Should().Equal(parameterNames);
            foreach (var parameter in metadata.RootElement.GetProperty("parameters").EnumerateArray()
                         .Where(item => item.GetProperty("dataType").GetString() == "GPFeatureRecordSetLayer"))
            {
                var schema = parameter.GetProperty("defaultValue");
                schema.GetProperty("features").GetArrayLength().Should().Be(0);
                schema.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
                var soapParameter = tool.Element("ParameterInfo")!.Elements("GPParameterInfo")
                    .Single(item => item.Element("Name")!.Value == parameter.GetProperty("name").GetString());
                var geometry = soapParameter.Descendants("GeometryDef").Single();
                geometry.Element("GeometryType")!.Value.Should().Be(schema.GetProperty("geometryType").GetString());
                geometry.Element("SpatialReference")!.Element("WKID")!.Value.Should().Be("4326");
                soapParameter.Descendants("Records").Single().Elements().Should().BeEmpty();
            }

            const string points = "-157.858333,21.306944;-157.862,21.31";
            var route = task == "FindRoutes";
            var inputs = new Dictionary<string, string>
            {
                ["f"] = "json",
                [route ? "Stops" : "Facilities"] = points,
                [route ? "Measurement_Units" : "Break_Units"] = "Minutes",
            };
            if (!route)
            {
                inputs["Break_Values"] = "2 5";
                inputs["Travel_Direction"] = "Away From Facility";
            }
            using var submit = await client.PostAsync(taskUrl + "/submitJob", new FormUrlEncodedContent(inputs));
            var submitText = await submit.Content.ReadAsStringAsync();
            submit.StatusCode.Should().Be(HttpStatusCode.OK, submitText);
            using var submitted = JsonDocument.Parse(submitText);
            var jobId = submitted.RootElement.GetProperty("jobId").GetString()!;
            using var terminal = await GPServerJobWait.UntilRestSucceededAsync(client,
                $"{taskUrl}/jobs/{jobId}?f=json", jobId, fixture.GetService<IExecutionJobStore>());
            terminal.RootElement.GetProperty("results").GetProperty(output).GetProperty("paramUrl").GetString()
                .Should().Be("results/" + output);

            using var resultResponse = await client.GetAsync($"{taskUrl}/jobs/{jobId}/results/{output}?f=json");
            using var result = JsonDocument.Parse(await resultResponse.Content.ReadAsStringAsync());
            result.RootElement.GetProperty("dataType").GetString().Should().Be("GPFeatureRecordSetLayer");
            var features = result.RootElement.GetProperty("value").GetProperty("features").EnumerateArray().ToArray();
            features.Should().NotBeEmpty();
            using var solvedResponse = await client.GetAsync($"{taskUrl}/jobs/{jobId}/results/Solve_Succeeded?f=json");
            using var solved = JsonDocument.Parse(await solvedResponse.Content.ReadAsStringAsync());
            solved.RootElement.GetProperty("dataType").GetString().Should().Be("GPBoolean");
            solved.RootElement.GetProperty("value").GetBoolean().Should().BeTrue();

            var solveOperation = route ? "solve" : "solveServiceArea";
            using var oracleResponse = await client.PostAsync($"/rest/services/{ServiceId}/NAServer/{solver}/{solveOperation}",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["f"] = "json",
                    [route ? "stops" : "facilities"] = points,
                    ["defaultBreaks"] = "2,5",
                }));
            var oracleText = await oracleResponse.Content.ReadAsStringAsync();
            oracleResponse.StatusCode.Should().Be(HttpStatusCode.OK, oracleText);
            using var oracle = JsonDocument.Parse(oracleText);
            oracle.RootElement.TryGetProperty("error", out _).Should().BeFalse(oracleText);
            var expected = oracle.RootElement.GetProperty(oracleOutput).GetProperty("features").EnumerateArray().ToArray();
            features.Should().HaveCount(expected.Length);
            for (var index = 0; index < features.Length; index++)
            {
                var shape = route ? "paths" : "rings";
                features[index].GetProperty("geometry").GetProperty(shape).GetRawText()
                    .Should().Be(expected[index].GetProperty("geometry").GetProperty(shape).GetRawText());
                foreach (var field in route ? new[] { "Total_Length", "Total_TravelTime" } : new[] { "FacilityID", "FromBreak", "ToBreak" })
                {
                    features[index].GetProperty("attributes").GetProperty(field).GetDouble()
                        .Should().Be(expected[index].GetProperty("attributes").GetProperty(field).GetDouble());
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
            await DeleteControlPlaneKeysAsync(GPServerRedisTestConnection.For(redis));
        }
    }
}
