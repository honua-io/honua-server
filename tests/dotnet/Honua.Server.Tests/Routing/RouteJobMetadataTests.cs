// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;

namespace Honua.Server.Tests.Routing;

public sealed class RouteJobMetadataTests
{
    [Theory]
    [InlineData(RoutingProcessDefinitions.ClosestFacilities)]
    [InlineData(RoutingProcessDefinitions.OdCostMatrix)]
    [InlineData(RoutingProcessDefinitions.LocationAllocation)]
    public void OtherRoutingTasks_RejectCustomTravelModeInsteadOfUsingDefault(string processId)
    {
        var definition = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == processId);
        Assert.Throws<GeoprocessingValidationException>(() => RoutingAsyncWebTools.Translate(definition,
            new Dictionary<string, string> { ["Travel_Mode"] = "Custom" }, null));
    }

    [Fact]
    public void NamedOutputs_IncludeOnlySupportedAndEmptyOptionalOutputs()
    {
        Assert.Equal(new[] { "Output_Routes", "Output_Stops", "Output_Directions", "Output_Route_Edges", "Solve_Succeeded" },
            RoutingAsyncWebTools.Outputs(RoutingProcessDefinitions.Route));
    }

    [Theory]
    [InlineData("Populate_Directions")]
    [InlineData("Populate_Route_Edges")]
    public void UnsupportedOptionalGeometry_IsRejectedWhenRequested(string parameter)
    {
        var route = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == RoutingProcessDefinitions.Route);
        Assert.Throws<GeoprocessingValidationException>(() => RoutingAsyncWebTools.Translate(route,
            new Dictionary<string, string> { [parameter] = "true" }, null));
        var translated = RoutingAsyncWebTools.Translate(route, new Dictionary<string, string> { [parameter] = "false" }, null);
        Assert.False(translated.ContainsKey(parameter));
    }
    [Fact]
    public void Route_RejectsCustomTravelModeAndDoesNotAdvertiseCutoff()
    {
        var route = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == RoutingProcessDefinitions.Route);
        Assert.DoesNotContain(route.Parameters, x => x.Name == "cutoff");
        Assert.Throws<GeoprocessingValidationException>(() => RoutingAsyncWebTools.Translate(route,
            new Dictionary<string, string> { ["Travel_Mode"] = "Custom" }, null));
    }

    [Fact]
    public void StopTemplate_DeclaresSupportedGroupAndSequenceFields()
    {
        var fields = RoutingAsyncWebTools.DefaultFeatureSet("stops").GetProperty("fields").EnumerateArray()
            .ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeString", fields["RouteName"]);
        Assert.Equal("esriFieldTypeInteger", fields["Sequence"]);
    }

    [Fact]
    public void RouteMeasurementUnits_AreRequiredAndDefaultToSupportedMinutes()
    {
        var route = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == RoutingProcessDefinitions.Route);
        var task = GPServerEndpoints.BuildTaskInfo("FindRoutes", route);
        var measurement = task.Parameters!.Single(x => x.Name == "Measurement_Units");
        Assert.Equal("esriGPParameterTypeRequired", measurement.ParameterType);
        Assert.Equal("Minutes", measurement.DefaultValue!.Value.GetString());
        Assert.Equal(new[] { "Seconds", "Minutes", "Hours", "Days" }, measurement.ChoiceList);
    }

    [Theory]
    [InlineData("Populate_Route_Edges")]
    [InlineData("Populate_Directions")]
    [InlineData("Save_Route_Data")]
    [InlineData("Save_Output_Network_Analysis_Layer")]
    public void FalseOnlyControls_AreAdvertisedAndRejectTrue(string name)
    {
        var route = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == RoutingProcessDefinitions.Route);
        var parameter = GPServerEndpoints.BuildTaskInfo("FindRoutes", route).Parameters!.Single(x => x.Name == name);
        Assert.Equal("GPBoolean", parameter.DataType);
        Assert.False(parameter.DefaultValue!.Value.GetBoolean());
        Assert.Equal("esriGPParameterTypeOptional", parameter.ParameterType);
        Assert.Throws<GeoprocessingValidationException>(() => RoutingAsyncWebTools.Translate(route,
            new Dictionary<string, string> { [name] = "true" }, null));
        var translated = RoutingAsyncWebTools.Translate(route, new Dictionary<string, string> { [name] = "false" }, null);
        Assert.False(translated.ContainsKey(name));
    }

}
