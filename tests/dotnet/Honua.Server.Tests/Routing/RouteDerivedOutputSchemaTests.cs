// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
using System.Text;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;

namespace Honua.Server.Tests.Routing;

public sealed class RouteDerivedOutputSchemaTests
{
    [Fact]
    public void TaskMetadata_DescribesDerivedOutputsAndActualRouteFields()
    {
        var definition = RoutingProcessDefinitions.Create().Single(x => x.ProcessId == RoutingProcessDefinitions.Route);
        var task = GPServerEndpoints.BuildTaskInfo("FindRoutes", definition);
        var outputs = task.Parameters!.Where(x => x.Direction == "esriGPParameterDirectionOutput").ToArray();
        Assert.All(outputs, x => Assert.Equal("esriGPParameterTypeDerived", x.ParameterType));
        var route = outputs.Single(x => x.Name == "Output_Routes").DefaultValue!.Value;
        Assert.Equal("esriGeometryPolyline", route.GetProperty("geometryType").GetString());
        var fields = route.GetProperty("fields").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeString", fields["Name"]);
        Assert.Equal("esriFieldTypeInteger", fields["StopCount"]);
        Assert.Equal("esriFieldTypeDouble", fields["Total_Time"]);
        Assert.Equal("esriFieldTypeDouble", fields["Total_Distance"]);
        Assert.Equal("esriGeometryPoint", outputs.Single(x => x.Name == "Output_Stops").DefaultValue!.Value.GetProperty("geometryType").GetString());
        Assert.Null(outputs.Single(x => x.Name == "Solve_Succeeded").DefaultValue);
    }

    [Theory]
    [InlineData("Output_Directions")]
    [InlineData("Output_Route_Edges")]
    public void EmptyOptionalResults_RetainDeclaredPolylineSchema(string name)
    {
        var schema = RoutingAsyncWebTools.RouteOutputSchema(name);
        var value = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"type\":\"FeatureCollection\",\"features\":[]}"));
        var result = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer, value, 4326, schema.GetRawText());
        Assert.Equal(schema.GetProperty("geometryType").GetString(), result.GetProperty("geometryType").GetString());
        Assert.Empty(result.GetProperty("features").EnumerateArray());
        Assert.Equal("esriFieldTypeOID", result.GetProperty("fields")[0].GetProperty("type").GetString());
    }
    [Fact]
    public void RouteResult_StopCountIsPublishedAsInteger()
    {
        var schema = RoutingAsyncWebTools.RouteOutputSchema("Output_Routes");
        const string geoJson = """
            {"type":"FeatureCollection","features":[{"type":"Feature","geometry":{"type":"LineString","coordinates":[[0,0],[1,1]]},"properties":{"Name":"A","StopCount":2,"Total_Time":4.0,"Total_Distance":1.0}}]}
            """;
        var value = "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(geoJson));
        var result = GPServerEsriOutputTranslation.Translate(ArtifactKind.FeatureLayer, value, 4326, schema.GetRawText());
        var fields = result.GetProperty("fields").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("type").GetString());
        Assert.Equal("esriFieldTypeInteger", fields["StopCount"]);
        Assert.Equal(2, result.GetProperty("features")[0].GetProperty("attributes").GetProperty("StopCount").GetInt32());
    }

}
