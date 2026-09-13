// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Protocol(TestProtocols.GPServer)]
public sealed partial class GPServerSoapEndpointsTests
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolInfos_ArcPyToolIdentifiers_AreValidUniqueAndRoundTrip()
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "GetToolInfos");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tasks = XDocument.Parse(await response.Content.ReadAsStringAsync()).Descendants("GPToolInfo").ToArray();
        var names = tasks.Select(task => task.Element("Name")!.Value).ToArray();
        names.Should().OnlyHaveUniqueItems();
        foreach (var name in names)
        {
            // ArcPy 3.7 generates `def <Name>(...)` directly. Dots and hyphens
            // in a canonical process id caused SyntaxError in the real import.
            name.Should().MatchRegex("^[A-Za-z_][A-Za-z0-9_]*$");
        }

        names.Should().Contain("Buffer");
        foreach (var parameter in tasks.SelectMany(task => task.Element("ParameterInfo")!.Elements("GPParameterInfo")))
        {
            var value = parameter.Element("Value");
            value.Should().NotBeNull("ArcPy needs a concrete parameter value even when its default is unset");
            value!.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value
                .Should().Be("tns:" + parameter.Element("DataType")!.Value);
            if (parameter.Element("DataType")!.Value == "GPMultiValue")
            {
                value.Element("MemberDataType")!.Value.Should().Be("GPString");
            }
        }

        var canonicalBuffer = tasks.Single(task => task.Element("DisplayName")!.Value == "Buffer" && task.Element("Name")!.Value != "Buffer");
        var publishedName = canonicalBuffer.Element("Name")!.Value;
        using var detail = await PostAsync(client, "GetToolInfo", $"<ToolName>{publishedName}</ToolName>");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = XDocument.Parse(await detail.Content.ReadAsStringAsync()).Descendants(XName.Get("GetToolInfoResponse", ArcGis)).Single().Element("Result")!;
        result.Element("Name")!.Value.Should().Be(publishedName);
        result.Element("ParameterInfo")!.ToString().Should().Be(canonicalBuffer.Element("ParameterInfo")!.ToString());
    }

    [IntegrationTheory]
    [InlineData(Soap11, "text/xml", ArcGis)]
    [InlineData(Soap12, "application/soap+xml", ArcGis)]
    [InlineData(Soap11, "text/xml", "http://www.esri.com/schemas/ArcGIS/9.0")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "GetToolInfos")]
    public async Task GetToolInfos_ArcPyEnvelope_ReturnsCanonicalTaskAndParameterMetadata(string soap, string contentType, string arcGis)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, "GetToolInfos", soap: soap, contentType: contentType, arcGis: arcGis);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        var document = XDocument.Parse(text);
        document.Root!.Name.Should().Be(XName.Get("Envelope", soap));
        var result = document.Descendants(XName.Get("GetToolInfosResponse", arcGis)).Single().Element("Result")!;
        var tasks = result.Elements("GPToolInfo").ToArray();
        tasks.Select(task => task.Element("Name")!.Value).Should().Contain("Buffer").And.NotContain("source.geojson");
        using var restResponse = await client.GetAsync("/rest/services/alpha/GPServer?f=json");
        using var rest = JsonDocument.Parse(await restResponse.Content.ReadAsStringAsync());
        tasks.Select(task => task.Element("Name")!.Value).Should().Equal(rest.RootElement.GetProperty("tasks").EnumerateArray().Select(task => task.GetString()), "SOAP and REST must publish the same callable names");
        var buffer = tasks.Single(task => task.Element("Name")!.Value == "Buffer");
        buffer.Elements().Select(element => element.Name.LocalName).Should().Equal("Name", "DisplayName", "Category", "Help", "ParameterInfo");
        using var parameterResponse = await client.GetAsync("/rest/services/alpha/GPServer/Buffer?f=json");
        using var metadata = JsonDocument.Parse(await parameterResponse.Content.ReadAsStringAsync());
        var expected = metadata.RootElement.GetProperty("parameters").EnumerateArray().ToArray();
        var parameters = buffer.Element("ParameterInfo")!.Elements("GPParameterInfo").ToArray();
        parameters.Should().HaveCount(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            parameters[index].Element("Name")!.Value.Should().Be(expected[index].GetProperty("name").GetString());
            parameters[index].Element("DataType")!.Value.Should().Be(expected[index].GetProperty("dataType").GetString());
            parameters[index].Element("Direction")!.Value.Should().Be(expected[index].GetProperty("direction").GetString());
            parameters[index].Element("ParamType")!.Value.Should().Be(expected[index].GetProperty("parameterType").GetString());
            var defaultValue = expected[index].GetProperty("defaultValue");
            if (defaultValue.ValueKind == JsonValueKind.Null)
            {
                parameters[index].Element("Value").Should().NotBeNull();
                parameters[index].Element("Value")!.Element("Value").Should().BeNull();
            }
            else
            {
                parameters[index].Element("Value")!.Element("Value")!.Value.Should().Be(
                    defaultValue.ValueKind == JsonValueKind.String ? defaultValue.GetString() : defaultValue.GetRawText());
            }
        }
    }

    [IntegrationTheory]
    [InlineData("GetTaskInfos", "", "GPToolInfo")]
    [InlineData("GetToolNames", "", "String")]
    [InlineData("GetTaskNames", "", "String")]
    [InlineData("GetToolInfo", "<ToolName>Buffer</ToolName>", "Name")]
    [InlineData("GetExecutionType", "", null)]
    [InlineData("GetResultMapServerName", "", null)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [InterfaceOperation(TestProtocols.GPServer, "GetTaskInfos")]
    [InterfaceOperation(TestProtocols.GPServer, "GetToolNames")]
    [InterfaceOperation(TestProtocols.GPServer, "GetTaskNames")]
    [InterfaceOperation(TestProtocols.GPServer, "GetToolInfo")]
    [InterfaceOperation(TestProtocols.GPServer, "GetExecutionType")]
    [InterfaceOperation(TestProtocols.GPServer, "GetResultMapServerName")]
    public async Task DiscoveryOperation_ValidRequest_ReturnsTypedResult(string operation, string arguments, string? child)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, arguments);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        var result = XDocument.Parse(body).Descendants(XName.Get(operation + "Response", ArcGis)).Single().Element("Result")!;
        if (child is not null)
        {
            result.Elements(child).Should().NotBeEmpty();
        }
        else
        {
            result.Value.Should().Be(operation == "GetExecutionType" ? "esriExecutionTypeAsynchronous" : "");
        }
    }

    [IntegrationTheory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("other-role", HttpStatusCode.Forbidden)]
    [InlineData("gp-reader", HttpStatusCode.OK)]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task GetToolInfos_ProtectedService_EnforcesCanonicalAuthorization(string? role, HttpStatusCode expected)
    {
        var policy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["gp-reader"]);
        using var factory = ServiceRbacTestFixture.CreateFactory(() => new RbacTestLayerCatalog(alphaServiceMetadata: policy));
        using var client = role is null ? factory.CreateClient() : ServiceRbacTestFixture.CreateClient(factory, role);
        using var response = await PostAsync(client, "GetToolInfos");
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        if (expected != HttpStatusCode.OK)
        {
            XDocument.Parse(text).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
            text.Should().NotContain("GPToolInfo").And.NotContain("geometry.buffer");
        }
    }

    [IntegrationTheory]
    [InlineData("GetToolInfo", "<ToolName>missing-task</ToolName>", HttpStatusCode.NotFound)]
    [InlineData("GetToolInfo", "<ToolName>source.geojson</ToolName>", HttpStatusCode.NotFound)]
    [InlineData("GetToolInfo", "", HttpStatusCode.BadRequest)]
    [InlineData("GetToolInfo", "<ToolName>Buffer</ToolName><ToolName>Clip</ToolName>", HttpStatusCode.BadRequest)]
    [InlineData("GetToolInfos", "<unexpected />", HttpStatusCode.BadRequest)]
    [InlineData("UnsupportedOperation", "", HttpStatusCode.NotImplemented)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task Request_InvalidOrUnsupportedOperation_ReturnsSoapFault(string operation, string arguments, HttpStatusCode expected)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = ServiceRbacTestFixture.CreateClient(factory, "alpha-reader");
        using var response = await PostAsync(client, operation, arguments);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        XDocument.Parse(text).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
    }

    [IntegrationTheory]
    [InlineData("<broken", "text/xml", HttpStatusCode.BadRequest)]
    [InlineData("<!DOCTYPE x [<!ENTITY x SYSTEM 'file:///must-not-be-read'>]><x>&x;</x>", "text/xml", HttpStatusCode.BadRequest)]
    [InlineData("<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body/><soap:Body/></soap:Envelope>", "text/xml", HttpStatusCode.BadRequest)]
    [InlineData("<soap:Envelope xmlns:soap='http://schemas.xmlsoap.org/soap/envelope/'><soap:Body><x:GetToolInfos xmlns:x='urn:unsupported'/></soap:Body></soap:Envelope>", "text/xml", HttpStatusCode.BadRequest)]
    [InlineData("{}", "application/json", HttpStatusCode.UnsupportedMediaType)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task Request_MalformedEnvelope_ReturnsSafeSoapFault(string xml, string contentType, HttpStatusCode expected)
    {
        using var factory = ServiceRbacTestFixture.CreateFactory();
        using var client = factory.CreateClient();
        using var content = new StringContent(xml, Encoding.UTF8, contentType);
        using var response = await client.PostAsync("/services/alpha/GPServer", content);
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(expected, text);
        XDocument.Parse(text).Descendants(XName.Get("Fault", Soap11)).Should().ContainSingle();
        text.Should().NotContain("file://").And.NotContain("must-not-be-read").And.NotContain("StackTrace");
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string operation, string arguments = "",
        string soap = Soap11, string contentType = "text/xml", string arcGis = ArcGis)
    {
        // Same qualified operation / unqualified argument shape captured from ArcPy 3.7.
        var xml = $"""
            <?xml version="1.0" encoding="utf-8" ?><soap:Envelope xmlns:soap="{soap}" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:tns="{arcGis}"><soap:Body><tns:{operation}>{arguments}</tns:{operation}></soap:Body></soap:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/services/alpha/GPServer");
        request.Headers.Add("SOAPAction", "\"\"");
        request.Content = new StringContent(xml, Encoding.UTF8, contentType);
        return await client.SendAsync(request);
    }
}
