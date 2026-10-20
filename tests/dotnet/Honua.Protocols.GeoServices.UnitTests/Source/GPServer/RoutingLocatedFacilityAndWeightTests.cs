// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Routing.Features.Routing.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class RoutingLocatedFacilityAndWeightTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void DemandWeight_IsExposedInBothRestAndSoapWithItsCanonicalNumericType()
    {
        var process = RoutingProcessDefinitions.Create().Single(p => p.ProcessId == RoutingProcessDefinitions.LocationAllocation);
        var task = GPServerEndpoints.BuildTaskInfo("SolveLocationAllocation", process);
        var parameter = task.Parameters!.Single(p => p.Name == "Demand_Points");
        var field = parameter.DefaultValue!.Value.GetProperty("fields").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "Weight");
        field.GetProperty("type").GetString().Should().Be("esriFieldTypeDouble");
        field.GetProperty("defaultValue").GetDouble().Should().Be(1);
        var soap = GPServerSoapEndpoints.BuildToolInfo(task).Descendants("GPParameterInfo")
            .Single(p => p.Element("Name")!.Value == "Demand_Points");
        soap.Descendants("Field").Single(f => f.Element("Name")!.Value == "Weight")
            .Element("Type")!.Value.Should().Be("esriFieldTypeDouble");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void ServiceAreaFacilities_HaveACompleteNativePointSchemaInBothCarriers()
    {
        var task = NAServerMetadata.BuildUtilityTaskInfo("GenerateServiceAreas").Deserialize(GPServerJsonContext.Default.GPTaskInfoResponse)!;
        var output = task.Parameters!.Single(p => p.Name == "Output_Facilities");
        output.Direction.Should().Be("esriGPParameterDirectionOutput");
        output.DefaultValue!.Value.GetProperty("geometryType").GetString().Should().Be("esriGeometryPoint");
        var soap = GPServerSoapEndpoints.BuildToolInfo(task).Descendants("GPParameterInfo")
            .Single(p => p.Element("Name")!.Value == "Output_Facilities");
        soap.Descendants("SpatialReference").Single().Element("WKID")!.Value.Should().Be("4326");
        soap.Descendants("Field").Select(f => f.Element("Name")!.Value)
            .Should().Contain(["ObjectID", "FacilityOID", "Name", "Status", "NetworkVertexID"]);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{layerName}/solveServiceArea")]
    public void LocatedFacilities_UseActualProviderCoordinatesVertexAndProjection()
    {
        var result = new ServiceAreaSolveResult([])
        {
            Facilities = [new ServiceAreaFacility(3, 9007199254740993, new RoutePoint(1234.5, 6789.5))],
        };
        var mapped = NAServerResultMapping.MapServiceArea(result, 3857).Facilities!;
        mapped["spatialReference"]!["wkid"]!.GetValue<int>().Should().Be(3857);
        var feature = mapped["features"]![0]!;
        feature["geometry"]!["x"]!.GetValue<double>().Should().Be(1234.5);
        feature["geometry"]!["y"]!.GetValue<double>().Should().Be(6789.5);
        feature["attributes"]!["NetworkVertexID"]!.GetValue<string>().Should().Be("9007199254740993");
        feature["attributes"]!["ObjectID"]!.GetValue<int>().Should().Be(4);
        feature["attributes"]!["FacilityID"]!.GetValue<int>().Should().Be(3);
        feature["attributes"]!["Status"]!.GetValue<int>().Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{layerName}/solveServiceArea")]
    public void UnlocatedFacilities_DoNotBecomeInventedSuccessfulPoints()
    {
        var result = new ServiceAreaSolveResult([]) { Facilities = [new ServiceAreaFacility(0, null, null)] };
        var feature = NAServerResultMapping.MapServiceArea(result, 4326).Facilities!["features"]![0]!;
        feature["geometry"].Should().BeNull();
        feature["attributes"]!["Status"]!.GetValue<int>().Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{layerName}/solveServiceArea")]
    public void MissingProviderLocations_CannotBeReconstructedFromPolygonSuccess()
    {
        var result = new ServiceAreaSolveResult([new ServiceAreaPolygon(0, 0, 5,
            """{"type":"Polygon","coordinates":[[[0,0],[1,0],[0,1],[0,0]]]}""")]);
        NAServerResultMapping.MapServiceArea(result, 4326).Facilities!["features"]!.AsArray().Should().BeEmpty();
    }

    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /rest/services/{serviceId}/NAServer/{layerName}/solveServiceArea")]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    public void ForeignOrDuplicateCanonicalFacilityIdentity_IsRejected(int index, bool duplicate)
    {
        var facility = new ServiceAreaFacility(index, 9, new RoutePoint(1, 2));
        var result = new ServiceAreaSolveResult([]) { Facilities = duplicate ? [facility, facility] : [facility] };
        var act = () => NAServerResultMapping.MapServiceArea(result, 4326);
        act.Should().Throw<InvalidOperationException>();
    }
}
