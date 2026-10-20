// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class RoutingFeatureSchemaContractTests
{
    [Theory]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InlineData("FindRoutes", "Stops", "esriGeometryPoint")]
    [InlineData("FindRoutes", "Output_Routes", "esriGeometryPolyline")]
    [InlineData("GenerateServiceAreas", "Facilities", "esriGeometryPoint")]
    [InlineData("GenerateServiceAreas", "Service_Areas", "esriGeometryPolygon")]
    public void RoutingFeatureSchemas_RetainRestProjectionAndFieldsInSoap(
        string tool, string parameterName, string geometryType)
    {
        var task = NAServerMetadata.BuildUtilityTaskInfo(tool)
            .Deserialize(GPServerJsonContext.Default.GPTaskInfoResponse)!;
        var parameter = task.Parameters!.Single(p => p.Name == parameterName);
        var rest = parameter.DefaultValue!.Value;
        rest.GetProperty("geometryType").GetString().Should().Be(geometryType);
        rest.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(4326);
        rest.GetProperty("features").GetArrayLength().Should().Be(0);

        var value = GPServerSoapEndpoints.BuildToolInfo(task).Element("ParameterInfo")!
            .Elements("GPParameterInfo").Single(p => p.Element("Name")!.Value == parameterName)
            .Element("Value")!;
        var fields = value.Element("RecordSet")!.Element("Fields")!.Element("FieldArray")!
            .Elements("Field").ToArray();
        var shape = fields.Single(f => f.Element("Type")!.Value == "esriFieldTypeGeometry");
        shape.Element("GeometryDef")!.Element("GeometryType")!.Value.Should().Be(geometryType);
        shape.Element("GeometryDef")!.Element("SpatialReference")!.Element("WKID")!.Value.Should().Be("4326");
        fields.Where(f => f != shape).Select(f => f.Element("Name")!.Value).Should()
            .Equal(rest.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        value.Element("ShapeFieldName")!.Value.Should().Be(shape.Element("Name")!.Value);
        value.Element("RecordSet")!.Element("Records")!.HasElements.Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void MetadataFeatureDefault_RoundTripsProjectedGeometryAndAttributeValues()
    {
        var parameter = Parameter("""
            {"geometryType":"esriGeometryPoint","spatialReference":{"wkid":3857},
             "objectIdFieldName":"ObjectID","fields":[
               {"name":"ObjectID","type":"esriFieldTypeOID"},
               {"name":"Name","type":"esriFieldTypeString","length":80}],
             "features":[{"attributes":{"ObjectID":7,"Name":"synthetic stop"},
                          "geometry":{"x":1000,"y":2000}}]}
            """);
        var task = new GPTaskInfoResponse { Name = "Synthetic", Parameters = [parameter] };
        var value = GPServerSoapEndpoints.BuildToolInfo(task).Descendants("GPParameterInfo").Single().Element("Value")!;
        value.Name = "GPValue";
        var operation = new XElement("Execute",
            new XAttribute(XNamespace.Xmlns + "tns", "http://www.esri.com/schemas/ArcGIS/10.8"),
            new XAttribute(XNamespace.Xmlns + "xsd", "http://www.w3.org/2001/XMLSchema"),
            new XElement("ToolName", "Synthetic"), new XElement("Values", value));
        using var result = JsonDocument.Parse(GPServerSoapExecution.ReadSubmission(operation, task)["Stops"]);
        result.RootElement.GetProperty("spatialReference").GetProperty("wkid").GetInt32().Should().Be(3857);
        var feature = result.RootElement.GetProperty("features")[0];
        feature.GetProperty("attributes").GetProperty("ObjectID").GetInt32().Should().Be(7);
        feature.GetProperty("attributes").GetProperty("Name").GetString().Should().Be("synthetic stop");
        feature.GetProperty("geometry").GetProperty("x").GetDouble().Should().Be(1000);
        feature.GetProperty("geometry").GetProperty("y").GetDouble().Should().Be(2000);
    }

    [UnitTest]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void MetadataFeatureDefault_MissingProjectionCannotInventAReferenceSystem()
    {
        var task = new GPTaskInfoResponse { Name = "Synthetic", Parameters = [Parameter("""
            {"geometryType":"esriGeometryPoint","fields":[],"features":[]}
            """)] };
        var act = () => GPServerSoapEndpoints.BuildToolInfo(task);
        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void MetadataFeatureReference_DoesNotBecomeAnInventedInlineDataset()
    {
        var task = new GPTaskInfoResponse { Name = "Synthetic", Parameters = [Parameter("""
            {"url":"https://example.invalid/FeatureServer/0"}
            """)] };
        var value = GPServerSoapEndpoints.BuildToolInfo(task).Descendants("GPParameterInfo").Single().Element("Value")!;
        value.HasElements.Should().BeFalse();
    }

    private static GPParameterInfo Parameter(string value)
        => new()
        {
            Name = "Stops", DataType = "GPFeatureRecordSetLayer",
            Direction = "esriGPParameterDirectionInput", ParameterType = "esriGPParameterTypeRequired",
            DefaultValue = JsonDocument.Parse(value).RootElement.Clone()
        };
}
