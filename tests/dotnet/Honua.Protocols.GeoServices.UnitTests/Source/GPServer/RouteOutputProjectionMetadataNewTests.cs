// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

public sealed class RouteOutputProjectionMetadataNewTests
{
    [UnitTest]
    public void FindRoutes_AdvertisesProjectedEmptyOutputsInRestAndSoapMetadata()
    {
        var definition = RoutingProcessDefinitions.Create().Single(p => p.ProcessId == RoutingProcessDefinitions.Route);
        var task = GPServerEndpoints.BuildTaskInfo("FindRoutes", definition);
        var soap = GPServerSoapEndpoints.BuildToolInfo(task);
        foreach (var (name, geometry) in new[]
                 {
                     ("Output_Routes", "esriGeometryPolyline"),
                     ("Output_Stops", "esriGeometryPoint"),
                 })
        {
            var parameter = task.Parameters!.Single(p => p.Name == name);
            parameter.Direction.Should().Be("esriGPParameterDirectionOutput");
            var value = parameter.DefaultValue!.Value;
            value.GetProperty("geometryType").GetString().Should().Be(geometry);
            value.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
            value.GetProperty("features").GetArrayLength().Should().Be(0);
            var native = soap.Descendants("GPParameterInfo").Single(p => p.Element("Name")!.Value == name);
            var geometryDef = native.Descendants("GeometryDef").Single();
            geometryDef.Element("GeometryType")!.Value.Should().Be(geometry);
            geometryDef.Element("SpatialReference")!.Element("WKID")!.Value.Should().Be("4326");
            native.Descendants("Records").Single().HasElements.Should().BeFalse();
        }
        task.Parameters!.Single(p => p.Name == "Solve_Succeeded").DefaultValue.Should().BeNull();
        var routeFields = task.Parameters!.Single(p => p.Name == "Output_Routes").DefaultValue!.Value
            .GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString());
        routeFields.Should().Contain(new[] { "Name", "Total_Minutes", "Total_Kilometers" });
    }
}
