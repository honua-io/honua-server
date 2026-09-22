// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Server.Tests.Features.Protocols.GeoServices.NAServer;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.GeoServices.GPServer;

[Collection("Database.GeoServicesCatalog")]
[Protocol(TestProtocols.GPServer)]
public sealed class GPServerRoutingUtilitySoapTests : IClassFixture<GPServerRoutingUtilityFixture>
{
    private const string Soap11 = "http://schemas.xmlsoap.org/soap/envelope/";
    private const string Soap12 = "http://www.w3.org/2003/05/soap-envelope";
    private const string ArcGis = "http://www.esri.com/schemas/ArcGIS/10.8";
    private const string Utility = "NetworkAnalysisUtilities";
    private readonly GPServerRoutingUtilityFixture _fixture;

    public GPServerRoutingUtilitySoapTests(GPServerRoutingUtilityFixture fixture) => _fixture = fixture;

    [IntegrationTheory]
    [InlineData(Soap11, "text/xml", ArcGis)]
    [InlineData(Soap12, "application/soap+xml", ArcGis)]
    [InlineData(Soap11, "text/xml", "http://www.esri.com/schemas/ArcGIS/9.0")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task UtilityDiscovery_UsesSameSynchronousNamesAndTypedParameters(string soap, string contentType, string arcGis)
    {
        using var rest = await GetJsonAsync($"/rest/services/{Utility}/GPServer?f=json");
        rest.RootElement.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        var expectedNames = rest.RootElement.GetProperty("tasks").EnumerateArray().Select(item => item.GetString()).ToArray();
        expectedNames.Should().Equal("GetToolInfo", "GetTravelModes");
        var infos = await SoapAsync("GetToolInfos", soap: soap, contentType: contentType, arcGis: arcGis);
        infos.Elements("GPToolInfo").Select(item => item.Element("Name")!.Value).Should().Equal(expectedNames);
        var execution = await SoapAsync("GetExecutionType", soap: soap, contentType: contentType, arcGis: arcGis);
        execution.Value.Should().Be("esriExecutionTypeSynchronous");
        foreach (var task in infos.Elements("GPToolInfo"))
        {
            var name = task.Element("Name")!.Value;
            var detail = await SoapAsync("GetToolInfo", "<ToolName>" + name + "</ToolName>", soap: soap, contentType: contentType, arcGis: arcGis);
            detail.Element("Name")!.Value.Should().Be(name);
            XNode.DeepEquals(detail.Element("ParameterInfo"), task.Element("ParameterInfo")).Should().BeTrue();
            using var metadata = await GetJsonAsync($"/rest/services/{Utility}/GPServer/{name}?f=json");
            var expected = metadata.RootElement.GetProperty("parameters").EnumerateArray().ToArray();
            var actual = task.Element("ParameterInfo")!.Elements("GPParameterInfo").ToArray();
            actual.Should().HaveCount(expected.Length);
            for (var i = 0; i < expected.Length; i++)
            {
                actual[i].Element("Name")!.Value.Should().Be(expected[i].GetProperty("name").GetString());
                actual[i].Element("DataType")!.Value.Should().Be(expected[i].GetProperty("dataType").GetString());
                actual[i].Element("Direction")!.Value.Should().Be(expected[i].GetProperty("direction").GetString());
                actual[i].Element("ParamType")!.Value.Should().Be(expected[i].GetProperty("parameterType").GetString());
                actual[i].Element("Value")!.Attribute(XName.Get("type", "http://www.w3.org/2001/XMLSchema-instance"))!.Value
                    .Should().Be("tns:" + expected[i].GetProperty("dataType").GetString());
            }
        }
    }

    [IntegrationTheory]
    [InlineData("NetworkAnalysisUtilities")]
    [InlineData("Routing")]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecuteTravelModes_UsesRealRecordSetCodecAndSharedRoutingResult(string service)
    {
        _fixture.Jobs.ClearReceivedCalls();
        using var rest = await GetJsonAsync($"/rest/services/{service}/GPServer/GetTravelModes/execute?f=json");
        var results = rest.RootElement.GetProperty("results");
        var set = results[0].GetProperty("value");
        var expectedFields = set.GetProperty("fields").EnumerateArray().ToArray();
        var expectedRows = set.GetProperty("features").EnumerateArray().ToArray();
        expectedRows.Should().NotBeEmpty();
        var result = await SoapAsync("Execute", "<ToolName>GetTravelModes</ToolName><Values />", service: service);
        var values = result.Element("Values")!.Elements("GPValue").ToArray();
        values.Should().HaveCount(2);
        values[0].Element("OIDFieldName")!.Value.Should().Be("ObjectID");
        var recordSet = values[0].Element("RecordSet")!;
        var fields = recordSet.Element("Fields")!.Element("FieldArray")!.Elements("Field").ToArray();
        fields.Select(field => field.Element("Name")!.Value).Should().Equal(expectedFields.Select(field => field.GetProperty("name").GetString()));
        fields.Select(field => field.Element("Type")!.Value).Should().Equal(expectedFields.Select(field => field.GetProperty("type").GetString()));
        var rows = recordSet.Element("Records")!.Elements("Record").ToArray();
        rows.Should().HaveCount(expectedRows.Length);
        for (var row = 0; row < rows.Length; row++)
        {
            var cells = rows[row].Element("Values")!.Elements().ToArray();
            cells.Should().HaveCount(expectedFields.Length);
            for (var field = 0; field < cells.Length; field++)
            {
                cells[field].Value.Should().Be(expectedRows[row].GetProperty("attributes").GetProperty(expectedFields[field].GetProperty("name").GetString()!).ToString());
            }
        }
        values[1].Element("Value")!.Value.Should().Be(results[1].GetProperty("value").GetString());
        _fixture.Jobs.ReceivedCalls().Should().BeEmpty("synchronous routing metadata must not create or poll a fabricated job");
    }

    [IntegrationTest]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExecuteToolInfo_HonorsTypedInputsAndMatchesRoutingProviderResult()
    {
        _fixture.Jobs.ClearReceivedCalls();
        using var rest = await GetJsonAsync($"/rest/services/{Utility}/GPServer/GetToolInfo/execute?f=json&serviceName=asyncServiceArea&toolName=GenerateServiceAreas");
        var result = await SoapAsync("Execute", "<ToolName>GetToolInfo</ToolName><Values><GPValue xsi:type='tns:GPString'><Value>asyncServiceArea</Value></GPValue><GPValue xsi:type='tns:GPString'><Value>GenerateServiceAreas</Value></GPValue></Values>");
        var text = result.Element("Values")!.Element("GPValue")!.Element("Value")!.Value;
        using var actual = JsonDocument.Parse(text);
        actual.RootElement.GetRawText().Should().Be(rest.RootElement.GetProperty("results")[0].GetProperty("value").GetRawText());
        actual.RootElement.GetProperty("networkDataset").GetProperty("defaultCostAttribute").GetString().Should().Be("TravelTime");
        _fixture.Jobs.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("SubmitJob", "<ToolName>GetTravelModes</ToolName><Values />", 400)]
    [InlineData("GetJobStatus", "<JobID>invented</JobID>", 400)]
    [InlineData("Execute", "<ToolName>Buffer</ToolName><Values />", 404)]
    [InlineData("Execute", "<ToolName>GetToolInfo</ToolName><Values><GPValue xsi:type='tns:GPLong'><Value>7</Value></GPValue></Values>", 400)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    public async Task UnsupportedJobCatalogAndParameterForms_FaultWithoutCallingJobRuntime(string operation, string arguments, int status)
    {
        _fixture.Jobs.ClearReceivedCalls();
        using var response = await PostSoapAsync(_fixture.App.Client, operation, arguments);
        response.StatusCode.Should().Be((HttpStatusCode)status);
        var body = await response.Content.ReadAsStringAsync();
        XDocument.Parse(body).Descendants().Should().Contain(element => element.Name.LocalName == "Fault");
        body.Should().NotContain("JobID").And.NotContain("GPResult");
        _fixture.Jobs.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData(null, HttpStatusCode.Unauthorized)]
    [InlineData("beta-reader", HttpStatusCode.Forbidden)]
    [Operation(Operations.Security)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/execute")]
    public async Task ExistingProtectedPublication_UtilityAliasRetainsCanonicalAccessGate(string? role, HttpStatusCode expected)
    {
        var policy = ServiceRbacTestFixture.CreateServiceMetadata(readRoles: ["gp-reader"]);
        using var factory = ServiceRbacTestFixture.CreateFactory(() => new RbacTestLayerCatalog(alphaServiceMetadata: policy));
        using var client = role is null ? factory.CreateClient() : ServiceRbacTestFixture.CreateClient(factory, role);
        using var response = await client.GetAsync("/rest/services/alpha/GPServer/GetTravelModes/execute?f=json");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(expected == HttpStatusCode.Unauthorized ? 499 : (int)expected);
        body.RootElement.TryGetProperty("results", out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer")]
    [Endpoint("POST /services/{serviceId}/GPServer")]
    [Endpoint("GET /sharing/rest/portals/self")]
    public async Task MissingRoutingDependency_FailsBothTransportsAndOmitsPortalAdvertisement(bool removeProvider)
    {
        await using var app = new WebAppFixture().ConfigureServices(services =>
        {
            if (removeProvider)
            {
                services.RemoveAll<IRoutingProvider>();
            }
            else
            {
                services.RemoveAll<IRoutingProvider>();
                services.AddScoped<IRoutingProvider, TestRoutingProvider>();
                services.RemoveAll<INetworkDatasetResolver>();
            }
        });
        await app.InitializeAsync();
        using var rest = await app.Client.GetAsync($"/rest/services/{Utility}/GPServer?f=json");
        using var body = JsonDocument.Parse(await rest.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(503);
        body.RootElement.TryGetProperty("tasks", out _).Should().BeFalse();
        using var soap = await PostSoapAsync(app.Client, "Execute", "<ToolName>GetTravelModes</ToolName><Values />");
        soap.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        using var portal = await app.Client.GetAsync("/sharing/rest/portals/self?f=json");
        (await portal.Content.ReadAsStringAsync()).Should().NotContain("routingUtilities");
    }

    [IntegrationTheory]
    [InlineData("Routing")]
    [InlineData("NetworkAnalysisUtilities")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}")]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    [Endpoint("POST /rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel")]
    public async Task SyntheticUtility_RestJobRoutesNeverReachJobRuntime(string service)
    {
        _fixture.Jobs.ClearReceivedCalls();
        _fixture.Terminal.ClearReceivedCalls();
        var root = $"/rest/services/{service}/GPServer/GetTravelModes/jobs";
        foreach (var suffix in new[] { "", "/owned-looking-job", "/owned-looking-job/results/Travel_Modes", "/owned-looking-job/cancel" })
        {
            using var response = await _fixture.App.Client.GetAsync(root + suffix + "?f=json");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
            body.RootElement.TryGetProperty("jobs", out _).Should().BeFalse();
            body.RootElement.TryGetProperty("jobId", out _).Should().BeFalse();
        }
        using var post = await _fixture.App.Client.PostAsync(root + "/owned-looking-job/cancel?f=json", new FormUrlEncodedContent([]));
        using var postBody = JsonDocument.Parse(await post.Content.ReadAsStringAsync());
        postBody.RootElement.GetProperty("error").GetProperty("code").GetInt32().Should().Be(400);
        _fixture.Jobs.ReceivedCalls().Should().BeEmpty();
        _fixture.Terminal.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData("GetTravelModes")]
    [InlineData("gettravelmodes")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /rest/services/{serviceId}/GPServer/{taskName}")]
    public void CatalogProcessNamedLikeUtility_KeepsItsOrdinaryServiceIdentity(string name)
    {
        var definition = new BuiltInProcessCatalog().GetProcess("geometry.buffer")! with { ProcessId = "GetTravelModes" };
        var catalog = Substitute.For<IProcessCatalog>();
        catalog.ListProcesses().Returns(new[] { definition });
        GPServerEndpoints.IsNetworkAnalysisUtilityTask(catalog, false, name).Should().BeFalse();
        GPServerEndpoints.IsNetworkAnalysisUtilityTask(catalog, true, name).Should().BeTrue();
    }

    private async Task<JsonDocument> GetJsonAsync(string path)
    {
        using var response = await _fixture.App.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private async Task<XElement> SoapAsync(string operation, string arguments = "", string service = Utility,
        string soap = Soap11, string contentType = "text/xml", string arcGis = ArcGis)
    {
        using var response = await PostSoapAsync(_fixture.App.Client, operation, arguments, service, soap, contentType, arcGis);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        return XDocument.Parse(body).Descendants(XName.Get(operation + "Response", arcGis)).Single().Element("Result")!;
    }

    private static async Task<HttpResponseMessage> PostSoapAsync(HttpClient client, string operation, string arguments,
        string service = Utility, string soap = Soap11, string contentType = "text/xml", string arcGis = ArcGis)
    {
        var xml = $"<soap:Envelope xmlns:soap='{soap}' xmlns:tns='{arcGis}' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance'><soap:Body><tns:{operation}>{arguments}</tns:{operation}></soap:Body></soap:Envelope>";
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/services/{service}/GPServer");
        request.Headers.Add("SOAPAction", "\"\"");
        request.Content = new StringContent(xml, Encoding.UTF8, contentType);
        return await client.SendAsync(request);
    }
}

public sealed class GPServerRoutingUtilityFixture : IAsyncLifetime
{
    internal IGeoprocessingJobService Jobs { get; } = Substitute.For<IGeoprocessingJobService>();
    internal IGeoprocessingJobTerminalService Terminal { get; } = Substitute.For<IGeoprocessingJobTerminalService>();
    internal WebAppFixture App { get; }

    public GPServerRoutingUtilityFixture()
    {
        App = new WebAppFixture().ConfigureServices(services =>
        {
            services.RemoveAll<IRoutingProvider>();
            services.AddScoped<IRoutingProvider, TestRoutingProvider>();
            services.RemoveAll<IGeoprocessingJobService>();
            services.AddSingleton(Jobs);
            services.RemoveAll<IGeoprocessingJobTerminalService>();
            services.AddSingleton(Terminal);
        });
    }

    public Task InitializeAsync() => App.InitializeAsync();
    public Task DisposeAsync() => App.DisposeAsync();
}
