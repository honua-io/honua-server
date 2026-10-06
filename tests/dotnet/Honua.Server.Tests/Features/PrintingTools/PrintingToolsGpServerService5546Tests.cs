// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Xunit;

namespace Honua.Server.Tests.Features.PrintingTools;

/// <summary>
/// Utilities/PrintingTools is a GPServer service: service resource, task resources,
/// Utilities folder and service node, and the SOAP binding and catalog entries
/// sibling GP services already publish.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.PrintingTools)]
public sealed class PrintingToolsGpServerService5546Tests : IAsyncLifetime
{
    private const string ServicePath = "/rest/services/Utilities/PrintingTools/GPServer";
    private const string SoapPath = "/services/Utilities/PrintingTools/GPServer";
    private const string QualifiedName = "Utilities/PrintingTools";
    private const string ArcGisSoapNamespace = "http://www.esri.com/schemas/ArcGIS/10.8";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer")]
    [Endpoint("POST /rest/services/Utilities/PrintingTools/GPServer")]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task")]
    [Endpoint("GET /rest/services/Utilities/PrintingTools")]
    [Endpoint("POST /rest/services/Utilities/PrintingTools")]
    [Endpoint("GET /rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task")]
    public async Task PrintingTools_IsPublishedAsGpServerServiceAndCatalogNode()
    {
        using var service = await _client.GetAsync(ServicePath + "?f=json");
        var serviceBody = await service.Content.ReadAsStringAsync();
        service.StatusCode.Should().Be(HttpStatusCode.OK, serviceBody);
        using var serviceJson = JsonDocument.Parse(serviceBody);
        var root = serviceJson.RootElement;
        root.TryGetProperty("currentVersion", out _).Should().BeFalse();
        root.GetProperty("serviceDescription").GetString().Should().Be($"Geoprocessing service for {QualifiedName}");
        root.GetProperty("executionType").GetString().Should().Be("esriExecutionTypeSynchronous");
        root.GetProperty("capabilities").GetString().Should().BeEmpty();
        root.GetProperty("resultMapServerName").GetString().Should().BeEmpty();
        root.GetProperty("maximumRecords").GetInt32().Should().Be(1000);
        root.GetProperty("tasks").EnumerateArray().Select(task => task.GetString()).Should().Equal(
            "Export Web Map Task",
            "Get Layout Templates Info Task");

        using var form = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        using var posted = await _client.PostAsync(ServicePath, form);
        posted.StatusCode.Should().Be(HttpStatusCode.OK, await posted.Content.ReadAsStringAsync());

        using var layout = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Get%20Layout%20Templates%20Info%20Task?f=json");
        var layoutBody = await layout.Content.ReadAsStringAsync();
        layout.StatusCode.Should().Be(HttpStatusCode.OK, layoutBody);
        using var layoutJson = JsonDocument.Parse(layoutBody);
        layoutJson.RootElement.GetProperty("name").GetString().Should().Be("Get Layout Templates Info Task");
        layoutJson.RootElement.GetProperty("displayName").GetString().Should().Be("Get Layout Templates Info");
        var output = layoutJson.RootElement.GetProperty("parameters").EnumerateArray()
            .Should().ContainSingle().Subject;
        output.GetProperty("name").GetString().Should().Be("Output_JSON");
        output.GetProperty("dataType").GetString().Should().Be("GPString");
        output.GetProperty("direction").GetString().Should().Be("esriGPParameterDirectionOutput");

        using var exportTask = await _client.GetAsync(
            "/rest/services/Utilities/PrintingTools/GPServer/Export%20Web%20Map%20Task?f=json");
        var exportBody = await exportTask.Content.ReadAsStringAsync();
        exportTask.StatusCode.Should().Be(HttpStatusCode.OK, exportBody);
        using var exportJson = JsonDocument.Parse(exportBody);
        exportJson.RootElement.GetProperty("name").GetString().Should().Be("Export Web Map Task");
        exportJson.RootElement.GetProperty("displayName").GetString().Should().Be("Export Web Map");
        exportJson.RootElement.TryGetProperty("parameterType", out _).Should().BeFalse();
        var format = exportJson.RootElement.GetProperty("parameters").EnumerateArray()
            .Single(parameter => parameter.GetProperty("name").GetString() == "Format");
        format.GetProperty("defaultValue").GetString().Should().Be("PNG32");

        await AssertDirectoryNodeAsync("/rest/services/Utilities?f=json");
        using var nodeForm = new FormUrlEncodedContent([new KeyValuePair<string, string>("f", "json")]);
        using var nodePost = await _client.PostAsync("/rest/services/Utilities/PrintingTools", nodeForm);
        nodePost.StatusCode.Should().Be(HttpStatusCode.OK, await nodePost.Content.ReadAsStringAsync());
        await AssertDirectoryNodeAsync("/rest/services/Utilities/PrintingTools?f=json");

        using var directory = await _client.GetAsync("/rest/services?f=json");
        var directoryBody = await directory.Content.ReadAsStringAsync();
        directory.StatusCode.Should().Be(HttpStatusCode.OK, directoryBody);
        using var directoryJson = JsonDocument.Parse(directoryBody);
        directoryJson.RootElement.GetProperty("folders").EnumerateArray().Select(folder => folder.GetString())
            .Should().Contain("Utilities");
        var listed = directoryJson.RootElement.GetProperty("services").EnumerateArray()
            .Single(entry => entry.GetProperty("name").GetString() == QualifiedName);
        listed.GetProperty("type").GetString().Should().Be("GPServer");
        listed.GetProperty("url").GetString().Should().EndWith(ServicePath);
        listed.GetProperty("url").GetString().Should().NotContain("%2F");
    }

    [IntegrationTest]
    [Operation(Operations.Print)]
    [Endpoint("POST /services/Utilities/PrintingTools/GPServer")]
    [Endpoint("POST /services")]
    public async Task PrintingTools_SoapBindingAndCatalog_ListBothTasks()
    {
        var tools = await PostSoapAsync(SoapPath, "GetToolInfos");
        tools.StatusCode.Should().Be(HttpStatusCode.OK, tools.Body);
        var toolNames = tools.Document.Descendants().Where(element => element.Name.LocalName == "GPToolInfo")
            .Select(tool => tool.Elements().Single(element => element.Name.LocalName == "Name").Value)
            .ToArray();
        toolNames.Should().Equal("Export Web Map Task", "Get Layout Templates Info Task");
        var exportTool = tools.Document.Descendants().Single(element =>
            element.Name.LocalName == "GPToolInfo" &&
            element.Elements().Single(child => child.Name.LocalName == "Name").Value == "Export Web Map Task");
        exportTool.Descendants().Single(element => element.Name.LocalName == "Name" && element.Value == "Format")
            .Parent!.Elements().Single(element => element.Name.LocalName == "ChoiceList")
            .Elements().Select(element => element.Value).Should().Contain("PNG32");

        var executionType = await PostSoapAsync(SoapPath, "GetExecutionType");
        executionType.StatusCode.Should().Be(HttpStatusCode.OK, executionType.Body);
        executionType.Document.Descendants().Single(element => element.Name.LocalName == "Result")
            .Value.Should().Be("esriExecutionTypeSynchronous");

        var folders = await PostSoapAsync("/services", "GetFolders");
        folders.StatusCode.Should().Be(HttpStatusCode.OK, folders.Body);
        folders.Document.Descendants().Where(element => element.Name.LocalName == "String")
            .Select(element => element.Value).Should().Contain("Utilities");

        var descriptions = await PostSoapAsync("/services", "GetServiceDescriptions");
        descriptions.StatusCode.Should().Be(HttpStatusCode.OK, descriptions.Body);
        AssertSoapPrintingService(descriptions.Document);

        var folderDescriptions = await PostSoapAsync(
            "/services",
            "GetServiceDescriptionsEx",
            """<FolderName>Utilities</FolderName>""");
        folderDescriptions.StatusCode.Should().Be(HttpStatusCode.OK, folderDescriptions.Body);
        AssertSoapPrintingService(folderDescriptions.Document);
        folderDescriptions.Document.Descendants().Where(element => element.Name.LocalName == "ServiceDescription")
            .Should().ContainSingle();

        var templates = await PostSoapAsync(
            SoapPath,
            "Execute",
            """
            <ToolName>Get Layout Templates Info Task</ToolName>
            <Values />
            """);
        templates.StatusCode.Should().Be(HttpStatusCode.OK, templates.Body);
        templates.Document.Descendants().Should().Contain(element =>
            element.Name.LocalName == "Value" && element.Value.Contains("MAP_ONLY", StringComparison.Ordinal));
    }

    private async Task AssertDirectoryNodeAsync(string path)
    {
        using var response = await _client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("folders").GetArrayLength().Should().Be(0);
        var service = json.RootElement.GetProperty("services").EnumerateArray().Should().ContainSingle().Subject;
        service.GetProperty("name").GetString().Should().Be(QualifiedName);
        service.GetProperty("type").GetString().Should().Be("GPServer");
        service.GetProperty("url").GetString().Should().EndWith(ServicePath);
        service.GetProperty("url").GetString().Should().NotContain("%2F");
    }

    private static void AssertSoapPrintingService(XDocument document)
    {
        var description = document.Descendants().Where(element => element.Name.LocalName == "ServiceDescription")
            .Single(element => element.Elements().Single(child => child.Name.LocalName == "Name").Value == QualifiedName);
        description.Elements().Single(element => element.Name.LocalName == "Type").Value.Should().Be("GPServer");
        new Uri(description.Elements().Single(element => element.Name.LocalName == "Url").Value)
            .AbsolutePath.Should().Be(SoapPath);
        new Uri(description.Elements().Single(element => element.Name.LocalName == "RestUrl").Value)
            .AbsolutePath.Should().Be(ServicePath);
    }

    private async Task<(HttpStatusCode StatusCode, string Body, XDocument Document)> PostSoapAsync(
        string path,
        string operation,
        string? arguments = null)
    {
        var request = $"""
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Body>
                <{operation} xmlns="{ArcGisSoapNamespace}">{arguments}</{operation}>
              </soap:Body>
            </soap:Envelope>
            """;
        using var content = new StringContent(request, Encoding.UTF8, "text/xml");
        using var response = await _client.PostAsync(path, content);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body, XDocument.Parse(body));
    }
}
