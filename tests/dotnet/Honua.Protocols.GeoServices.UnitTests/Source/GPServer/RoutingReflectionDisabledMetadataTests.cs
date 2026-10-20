// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class RoutingReflectionDisabledMetadataTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InlineData("routing.closest-facilities", "FindClosestFacilities")]
    [InlineData("routing.od-cost-matrix", "GenerateOriginDestinationCostMatrix")]
    [InlineData("routing.location-allocation", "SolveLocationAllocation")]
    public void ProductionTaskMetadata_WithReflectionDisabled_RetainsEveryFeatureDefaultInRestAndSoap(
        string processId, string taskName)
    {
        JsonSerializer.IsReflectionEnabledByDefault.Should().BeFalse(
            "this regression must execute under the production JSON reflection setting");
        var definition = RoutingProcessDefinitions.Create().Single(process => process.ProcessId == processId);
        var task = GPServerEndpoints.BuildTaskInfo(taskName, definition);
        using var rest = JsonDocument.Parse(JsonSerializer.Serialize(task, GPServerJsonContext.Default.GPTaskInfoResponse));
        rest.RootElement.GetProperty("name").GetString().Should().Be(taskName);
        var soap = GPServerSoapEndpoints.BuildToolInfo(task);
        soap.Element("Name")!.Value.Should().Be(taskName);
        var inputs = task.Parameters!.Where(parameter => parameter.Direction == "esriGPParameterDirectionInput"
            && parameter.DataType == "GPFeatureRecordSetLayer").ToArray();
        inputs.Should().HaveCount(5);
        foreach (var parameter in inputs)
        {
            var actual = rest.RootElement.GetProperty("parameters").EnumerateArray()
                .Single(value => value.GetProperty("name").GetString() == parameter.Name).GetProperty("defaultValue");
            var expectedGeometry = parameter.Name switch
            {
                "Line_Barriers" => "esriGeometryPolyline",
                "Polygon_Barriers" => "esriGeometryPolygon",
                _ => "esriGeometryPoint",
            };
            actual.GetProperty("geometryType").GetString().Should().Be(expectedGeometry);
            actual.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            actual.GetProperty("features").GetArrayLength().Should().Be(0);
            var fields = actual.GetProperty("fields").EnumerateArray().ToArray();
            fields.Select(field => field.GetProperty("name").GetString()).Should().Equal(
                parameter.Name == "Demand_Points" ? ["OBJECTID", "Name", "Weight"] : ["OBJECTID", "Name"]);
            var value = soap.Descendants("GPParameterInfo")
                .Single(info => info.Element("Name")!.Value == parameter.Name).Element("Value")!;
            value.Descendants("GeometryType").Single().Value.Should().Be(expectedGeometry);
            value.Descendants("WKID").Single().Value.Should().Be("4326");
            value.Descendants("Field").Where(field => field.Element("Type")!.Value != "esriFieldTypeGeometry")
                .Select(field => field.Element("Name")!.Value).Should().Equal(fields.Select(field => field.GetProperty("name").GetString()));
            if (parameter.Name == "Demand_Points")
            {
                var weight = fields.Single(field => field.GetProperty("name").GetString() == "Weight");
                weight.GetProperty("type").GetString().Should().Be("esriFieldTypeDouble");
                weight.GetProperty("defaultValue").GetDouble().Should().Be(1);
                value.Descendants("Field").Single(field => field.Element("Name")!.Value == "Weight")
                    .Element("Type")!.Value.Should().Be("esriFieldTypeDouble");
            }
        }
    }
}
