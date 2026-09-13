// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class GPServerSoapExecutionTests
{
    private const string ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_PositionalValues_PreservePublishedInputNamesAndContext()
    {
        var xml = Submission("""
            <Values><GPValue xsi:type="tns:GPString"><Value>rectangle-wkb</Value></GPValue>
            <GPValue xsi:type="tns:GPLong"><Value>3857</Value></GPValue></Values>
            <EnvironmentValues><PropertyArray><PropertySetProperty><Key>outputCoordinateSystem</Key>
            <Value xsi:type="tns:GPLong"><Value>4326</Value></Value></PropertySetProperty></PropertyArray></EnvironmentValues>
            """);
        var parameters = GPServerSoapExecution.ReadSubmission(xml, TaskInfo());
        parameters.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["wkb"] = "rectangle-wkb",
            ["srid"] = "3857",
            ["env:outSR"] = "4326"
        });
    }

    [Theory]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [InlineData("<ToolName>duplicate</ToolName><Values/>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPDouble'><Value>3</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='xsd:GPString'><Value>x</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPString' xsi:nil='true'><Value>x</Value></GPValue></Values>")]
    [InlineData("<Values><GPValue xsi:type='tns:GPString'><Value>x</Value><Extra/></GPValue></Values>")]
    [InlineData("<Values/><Unexpected/>")]
    [InlineData("<Values/><Options><DensifyFeatures>true</DensifyFeatures></Options>")]
    [InlineData("<Values/><Options><ReturnData>invalid</ReturnData></Options>")]
    [InlineData("<Values/><Options><TransportType>esriGPTransportTypeEmbedded</TransportType></Options>")]
    [InlineData("<Values/><Options><ReturnData>true</ReturnData><ReturnData>false</ReturnData></Options>")]
    public void Submission_MalformedOrUnsupportedControls_AreRejected(string arguments)
    {
        var act = () => GPServerSoapExecution.ReadSubmission(Submission(arguments), TaskInfo());
        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_UnsetValues_DoNotInventDefaults()
    {
        var parameters = GPServerSoapExecution.ReadSubmission(Submission("""
            <Values><GPValue xsi:type="tns:GPString"/><GPValue xsi:type="tns:GPLong" xsi:nil="true"/></Values>
            """), TaskInfo());
        parameters.Should().BeEmpty();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Submission_ArcPyDefaultEnvironment_IsNeutralForScalarGeometryOnly()
    {
        var xml = Submission("<Values/>" + GPServerSoapRequestFixtures.ArcPyDefaultControls);
        var task = TaskInfo();
        // Public Esri metadata remains asynchronous for the mixed catalog.
        task.ExecutionType = GPServerExecutionPolicy.AsynchronousExecutionType;
        GPServerSoapExecution.ReadSubmission(xml, task, scalarGeometryTask: true).Should().BeEmpty();
        // A changed setting must reach the shared environment validator rather
        // than disappear with the client's defaults.
        xml.Descendants("PropertySetProperty").Single(property => property.Element("Key")!.Value == "nodata")
            .Element("Value")!.Element("Value")!.Value = "MAXIMUM";
        GPServerSoapExecution.ReadSubmission(xml, task, scalarGeometryTask: true)["env:nodata"].Should().Be("MAXIMUM");
        var act = () => GPServerSoapExecution.ReadSubmission(xml, task);
        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void Result_ScalarArtifact_PreservesExactValueAndEscapesXml()
    {
        var result = GPServerSoapExecution.BuildResult(
            [new GPResultResponse { ParamName = "outputScalar", DataType = "GPString", Value = "a<&b" }],
            [new GPJobMessage { Type = "esriJobMessageTypeWarning", Description = "warning<&" }]);
        var parsed = XElement.Parse(result.ToString());
        parsed.Element("Values")!.Element("GPValue")!.Element("Value")!.Value.Should().Be("a<&b");
        parsed.Element("Messages")!.Element("JobMessage")!.Element("MessageDesc")!.Value.Should().Be("warning<&");
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void ResultNames_PreserveRequestedOrder_AndRejectDuplicates()
    {
        var xml = XElement.Parse("<GetJobResult><JobID>job</JobID><ParameterNames><String>b</String><String>a</String></ParameterNames></GetJobResult>");
        GPServerSoapExecution.ReadResultNames(xml, ["a", "b"]).Should().Equal("b", "a");
        xml.Element("ParameterNames")!.Add(new XElement("String", "b"));
        var act = () => GPServerSoapExecution.ReadResultNames(xml, ["a", "b"]);
        act.Should().Throw<GeoprocessingValidationException>();
    }

    private static XElement Submission(string arguments) => XElement.Parse($"""
        <tns:SubmitJob xmlns:tns="{ArcGis}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
        <ToolName>Area</ToolName>{arguments}</tns:SubmitJob>
        """);

    private static GPTaskInfoResponse TaskInfo() => new()
    {
        Name = "Area",
        Parameters =
        [
            new() { Name = "wkb", DataType = "GPString", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "srid", DataType = "GPLong", Direction = "esriGPParameterDirectionInput" },
            new() { Name = "outputScalar", DataType = "GPString", Direction = "esriGPParameterDirectionOutput" }
        ]
    };
}
