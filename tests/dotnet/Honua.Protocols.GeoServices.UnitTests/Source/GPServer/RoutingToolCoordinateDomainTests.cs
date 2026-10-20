// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Protocols.GeoServices.Soap;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed class RoutingToolCoordinateDomainTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public void SoapToolMetadata_EmitsRoutingCoordinateDomainAfterParameters_WithoutChangingOrdinaryTools()
    {
        JsonSerializer.IsReflectionEnabledByDefault.Should().BeFalse();
        var tasks = new List<GPTaskInfoResponse>();
        foreach (var name in new[] { "FindRoutes", "GenerateServiceAreas" })
        {
            var json = NAServerMetadata.Serialize(NAServerMetadata.BuildUtilityTaskInfo(name), pretty: false);
            tasks.Add(JsonSerializer.Deserialize(json, GPServerJsonContext.Default.GPTaskInfoResponse)!);
        }
        foreach (var (processId, taskName) in new[]
        {
            ("routing.closest-facilities", "FindClosestFacilities"),
            ("routing.od-cost-matrix", "GenerateOriginDestinationCostMatrix"),
            ("routing.location-allocation", "SolveLocationAllocation")
        })
        {
            tasks.Add(GPServerEndpoints.BuildTaskInfo(taskName,
                RoutingProcessDefinitions.Create().Single(process => process.ProcessId == processId)));
        }
        var ordinary = new GPTaskInfoResponse
        {
            Name = "OrdinaryGeometryTool",
            DisplayName = "Ordinary geometry",
            Category = "geometry",
            Description = "Synthetic ordinary tool"
        };
        var ordinaryInfo = GPServerSoapEndpoints.BuildToolInfo(ordinary);
        ordinaryInfo.ToString(SaveOptions.DisableFormatting).Should().Be(
            "<GPToolInfo><Name>OrdinaryGeometryTool</Name><DisplayName>Ordinary geometry</DisplayName>" +
            "<Category>geometry</Category><Help>Synthetic ordinary tool</Help><ParameterInfo /></GPToolInfo>");

        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace arcGis = ArcGisSoapNamespaces.Current;
        XNamespace xsi = "http://www.w3.org/2001/XMLSchema-instance";
        var response = ArcGisSoapProtocol.CreateSoapResponse(soap, arcGis, "GetToolInfosResponse",
            new XElement("Result", tasks.Select(GPServerSoapEndpoints.BuildToolInfo), ordinaryInfo));
        var wire = XDocument.Parse(((ContentHttpResult)response).ResponseContent!);
        wire.Root!.GetNamespaceOfPrefix("tns").Should().Be(arcGis);
        var infos = wire.Descendants("GPToolInfo").ToArray();
        infos.Should().HaveCount(6);
        foreach (var info in infos.Where(info => info.Element("Name")!.Value != ordinary.Name))
        {
            info.Elements().Select(element => element.Name.LocalName).Should().Equal(
                "Name", "DisplayName", "Category", "Help", "ParameterInfo", "Extent");
            var extent = info.Element("Extent")!;
            extent.Attribute(xsi + "type")!.Value.Should().Be("tns:EnvelopeN");
            extent.Elements().Select(element => element.Name.LocalName).Should().Equal(
                "XMin", "YMin", "XMax", "YMax", "SpatialReference");
            extent.Element("XMin")!.Value.Should().Be("-180");
            extent.Element("YMin")!.Value.Should().Be("-90");
            extent.Element("XMax")!.Value.Should().Be("180");
            extent.Element("YMax")!.Value.Should().Be("90");
            var reference = extent.Element("SpatialReference")!;
            reference.Attribute(xsi + "type")!.Value.Should().Be("tns:GeographicCoordinateSystem");
            reference.Element("WKID")!.Value.Should().Be("4326");
            reference.Element("LatestWKID")!.Value.Should().Be("4326");
        }
        infos.Single(info => info.Element("Name")!.Value == ordinary.Name).ToString(SaveOptions.DisableFormatting)
            .Should().Be(ordinaryInfo.ToString(SaveOptions.DisableFormatting));
    }
}
