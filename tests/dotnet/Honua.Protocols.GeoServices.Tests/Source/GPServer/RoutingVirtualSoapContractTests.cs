// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class RoutingVirtualSoapContractTests(NAServerEndpointTestsFixture fixture)
    : IClassFixture<NAServerEndpointTestsFixture>
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";
    private static readonly string[] Tasks = ["FindRoutes", "GenerateServiceAreas", "GetTravelModes", "GetToolInfo"];

    [IntegrationTheory]
    [InlineData("/services/Routing/GPServer")]
    [InlineData("/arcgis/services/Routing/GPServer")]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolInfos_VirtualRouting_ExposesCompleteTypedParameters(string path)
    {
        using var response = await PostAsync(path, "GetToolInfos");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var infos = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("GPToolInfo").ToArray();
        infos.Select(info => info.Element("Name")!.Value).Should().Equal(Tasks);
        var route = infos.Single(info => info.Element("Name")!.Value == "FindRoutes");
        var parameters = route.Descendants("GPParameterInfo").ToArray();
        var stops = parameters.Single(parameter => parameter.Element("Name")!.Value == "Stops");
        stops.Element("DataType")!.Value.Should().Be("GPFeatureRecordSetLayer");
        stops.Element("Direction")!.Value.Should().Be("esriGPParameterDirectionInput");
        stops.Element("ParamType")!.Value.Should().Be("esriGPParameterTypeRequired");
        stops.Element("Value").Should().NotBeNull();
        parameters.Should().Contain(parameter => parameter.Element("Name")!.Value == "Output_Routes"
            && parameter.Element("Direction")!.Value == "esriGPParameterDirectionOutput");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolNames_VirtualRouting_MatchesThePublishedCallableTasks()
    {
        using var response = await PostAsync("/services/Routing/GPServer", "GetToolNames");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("Result").Single()
            .Elements("String").Select(value => value.Value).Should().Equal(Tasks);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolInfo_VirtualRouting_ReturnsTheNamedSolverDefinition()
    {
        using var response = await PostAsync("/services/Routing/GPServer", "GetToolInfo", new XElement("ToolName", "GenerateServiceAreas"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("Result").Single();
        result.Element("Name")!.Value.Should().Be("GenerateServiceAreas");
        result.Descendants("GPParameterInfo").Should().Contain(parameter => parameter.Element("Name")!.Value == "Facilities"
            && parameter.Element("DataType")!.Value == "GPFeatureRecordSetLayer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetExecutionType_VirtualRouting_MatchesTheSynchronousServiceContract()
    {
        using var response = await PostAsync("/services/Routing/GPServer", "GetExecutionType");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("Result").Single().Value
            .Should().Be("esriExecutionTypeSynchronous");
    }

    [IntegrationTheory]
    [InlineData("FindRoutes", "esriGeometryPolyline")]
    [InlineData("GenerateServiceAreas", "esriGeometryPolygon")]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task Execute_PointRecordSet_ReachesTheRoutingProviderAndReturnsTypedGeometry(string task, string geometryType)
    {
        // Synthetic WGS84 points exercise the SOAP-to-canonical-provider boundary.
        var values = new XElement("Values", PointRecordSet());
        if (task == "GenerateServiceAreas")
        {
            values.Add(new XElement("GPValue", new XAttribute(Xsi + "type", "tns:GPString"), new XElement("Value", "5")));
        }
        using var response = await PostAsync("/services/Routing/GPServer", "Execute", new XElement("ToolName", task), values);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var result = XDocument.Parse(body).Descendants("Result").Single();
        var outputs = result.Element("Values")!.Elements("GPValue").ToArray();
        outputs.Should().HaveCount(2);
        outputs[0].Attribute(Xsi + "type")!.Value.Should().Be("tns:GPFeatureRecordSetLayer");
        outputs[0].Descendants("GeometryType").Single().Value.Should().Be(geometryType);
        outputs[0].Descendants("Record").Should().NotBeEmpty();
        outputs[0].Descendants("X").Should().NotBeEmpty();
        if (task == "FindRoutes")
        {
            outputs[0].Descendants("Field").Select(field => field.Element("Name")!.Value)
                .Should().Contain(["Name", "Total_Length", "Total_TravelTime"]);
            outputs[0].Descendants("Record").Single().Element("Values")!.Elements("Value").Skip(1).Take(2)
                .Select(value => double.Parse(value.Value, System.Globalization.CultureInfo.InvariantCulture))
                .Should().OnlyContain(value => value > 0);
        }
        outputs[1].Attribute(Xsi + "type")!.Value.Should().Be("tns:GPBoolean");
        outputs[1].Element("Value")!.Value.Should().Be("true");
    }

    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task Execute_WrongStopsType_ReturnsAClientFault()
    {
        var values = new XElement("Values", new XElement("GPValue", new XAttribute(Xsi + "type", "tns:GPString"),
            new XElement("Value", "invalid stops")));
        using var response = await PostAsync("/services/Routing/GPServer", "Execute", new XElement("ToolName", "FindRoutes"), values);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("faultstring").Single().Value
            .Should().Contain("GPFeatureRecordSetLayer");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolInfo_UnpublishedTask_ReturnsNotFound()
    {
        using var response = await PostAsync("/services/Routing/GPServer", "GetToolInfo", new XElement("ToolName", "UnknownSolver"));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants(Soap + "Fault").Should().ContainSingle();
    }

    [IntegrationTheory]
    [InlineData(true, false, HttpStatusCode.Unauthorized)]
    [InlineData(true, true, HttpStatusCode.Unauthorized)]
    [InlineData(false, false, HttpStatusCode.NotFound)]
    [InlineData(false, true, HttpStatusCode.NotFound)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task CatalogCollision_DiscoveryAndExecutionCannotFallBackToPublicRouting(
        bool gpServerEnabled, bool stableIdCollision, HttpStatusCode expected)
    {
        var graph = new MetadataV2Graph
        {
            Services = [new MetadataV2Service
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = stableIdCollision ? "Routing" : "svc-private-routing",
                    Name = stableIdCollision ? "Private Route" : "Routing"
                },
                Status = new MetadataV2Status { Lifecycle = MetadataV2LifecycleStatus.Active },
                Protocols = gpServerEnabled ? ["GPServer"] : ["FeatureServer"],
                AccessPolicy = new AccessPolicy { AllowAnonymous = false, AllowedRoles = ["routing-reader"] }
            }]
        };
        var app = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IMetadataV2GraphProvider>();
            services.AddSingleton<IMetadataV2GraphProvider>(new TestMetadataV2GraphProvider(graph, "routing-soap-collision"));
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
        });
        try
        {
            app.ConfigureWebHost(builder => builder.UseSetting("HONUA_DEV_AUTH", "false"));
            await app.InitializeAsync();
            foreach (var method in new[] { "GetToolInfos", "Execute" })
            {
                var arguments = method == "Execute" ? new[] { new XElement("ToolName", "FindRoutes"), new XElement("Values", PointRecordSet()) } : [];
                using var response = await app.Client.PostAsync("/services/Routing/GPServer", SoapContent(method, arguments));
                var body = await response.Content.ReadAsStringAsync();
                response.StatusCode.Should().Be(expected, body);
                XDocument.Parse(body).Descendants(Soap + "Fault").Should().ContainSingle();
                body.Should().NotContain("GPToolInfo").And.NotContain("GPResult");
            }
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private static XElement PointRecordSet() => XElement.Parse("""
        <GPValue xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:tns="http://www.esri.com/schemas/ArcGIS/10.8" xsi:type="tns:GPFeatureRecordSetLayer">
          <RecordSet><Fields><FieldArray><Field><Name>Shape</Name><Type>esriFieldTypeGeometry</Type>
            <GeometryDef><GeometryType>esriGeometryPoint</GeometryType><HasZ>false</HasZ><HasM>false</HasM><SpatialReference><WKID>4326</WKID></SpatialReference></GeometryDef>
          </Field></FieldArray></Fields><Records>
            <Record><Values><Value xsi:type="tns:PointN"><X>0</X><Y>0</Y></Value></Values></Record>
            <Record><Values><Value xsi:type="tns:PointN"><X>0.01</X><Y>0</Y></Value></Values></Record>
          </Records></RecordSet><ShapeFieldName>Shape</ShapeFieldName>
        </GPValue>
        """);

    private Task<HttpResponseMessage> PostAsync(string path, string method, params XElement[] arguments)
        => fixture.App.Client.PostAsync(path, SoapContent(method, arguments));

    private static StringContent SoapContent(string method, params XElement[] arguments)
    {
        var document = new XDocument(new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "tns", ArcGis.NamespaceName),
            new XElement(Soap + "Body", new XElement(ArcGis + method, arguments))));
        return new StringContent(document.ToString(), Encoding.UTF8, "text/xml");
    }
}
