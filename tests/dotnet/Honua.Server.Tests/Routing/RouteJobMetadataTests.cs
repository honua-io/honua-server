// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;

namespace Honua.Server.Tests.Routing;

public sealed class RouteJobMetadataTests
{
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
}
