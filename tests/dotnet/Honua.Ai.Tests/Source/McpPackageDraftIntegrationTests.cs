// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// End-to-end guard for the deterministic package-draft tools through the real
/// <c>POST /mcp</c> pipeline and the real host composition (ADR-0076, #3255).
/// </summary>
/// <remarks>
/// This is the load-bearing check the ADR asks for. Dispatcher-level tests
/// construct the tools by hand and therefore cannot prove the host composed the
/// draft factories; tool-roster and capability-manifest checks compare names and
/// would pass against a tool that is advertised but permanently returns an
/// unavailable stub. Only a real call through the composed server proves the
/// tools return a package with a stable <c>map_…</c> / <c>app_…</c> identifier.
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpPackageDraftIntegrationTests : IAsyncLifetime
{
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
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"map-draft","method":"tools/call","params":{
                "name":"honua_create_map_package",
                "arguments":{
                  "templateId":"analysis_default",
                  "styleId":"style_choropleth",
                  "sourceBindings":[{"sourceId":"parcels","protocol":"ogc_features","url":"https://example.test/ogc"}],
                  "initialView":{"bbox":[-97.95,30.15,-97.55,30.55],"crs":4326}
                }
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("created");

        var packageId = structured.GetProperty("packageId").GetString();
        packageId.Should().StartWith("map_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://map-packages/" + packageId);

        var package = structured.GetProperty("package");
        package.GetProperty("mapPackageId").GetString().Should().Be(packageId);
        package.GetProperty("format").GetString().Should().Be("honua_map_package.v1");
        package.GetProperty("sourceBindings").GetArrayLength().Should().Be(1);
        package.GetProperty("initialView").GetProperty("crs").GetString().Should().Be("EPSG:4326");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"app-draft","method":"tools/call","params":{
                "name":"honua_create_app_package",
                "arguments":{
                  "templateId":"analysis_dashboard",
                  "mapPackageId":"map_5a90",
                  "boundArtifactIds":["artifact_summary_report"],
                  "runtimeConfig":{"title":"Flood Exposure Dashboard"}
                }
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("created");

        var packageId = structured.GetProperty("packageId").GetString();
        packageId.Should().StartWith("app_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://app-packages/" + packageId);

        var package = structured.GetProperty("package");
        package.GetProperty("appPackageId").GetString().Should().Be(packageId);
        package.GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        package.GetProperty("sharePolicy").GetProperty("visibility").GetString().Should().Be("private");
    }

    [IntegrationTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithNoArgumentsAtAll_StillCreatesAPackage()
    {
        // Nothing about draft creation requires a prompt any more, so the empty
        // structured call is a valid one.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"map-empty","method":"tools/call","params":{
                "name":"honua_create_map_package","arguments":{}
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");

        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("structuredContent").GetProperty("packageId").GetString().Should().StartWith("map_");
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    public async Task ResourcesRead_MapPackageDraftUri_ResolvesWithoutADeployment()
    {
        // ADR-0076 promises the returned identifier is addressable at its URI. Creating
        // without persisting passes the create half of that promise and fails this half
        // (honua-server#3262), so both halves are asserted in one call sequence.
        var resourceUri = await CreateAndReadUriAsync("""
            {"jsonrpc":"2.0","id":"map-persist","method":"tools/call","params":{
                "name":"honua_create_map_package",
                "arguments":{"templateId":"analysis_default"}
            }}
            """, "map_", "honua://map-packages/");

        var body = await ReadResourceAsync(resourceUri);

        body.GetProperty("packageKind").GetString().Should().Be("map_package");
        body.GetProperty("resourceUri").GetString().Should().Be(resourceUri);
        body.GetProperty("packageStatus").GetString().Should().Be("draft");
        // A draft is reachable precisely because it does not need a deployment yet.
        body.GetProperty("deploymentCount").GetInt32().Should().Be(0);
        body.GetProperty("deploymentResourceUris").GetArrayLength().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    public async Task ResourcesRead_AppPackageDraftUri_ResolvesWithoutADeployment()
    {
        var resourceUri = await CreateAndReadUriAsync("""
            {"jsonrpc":"2.0","id":"app-persist","method":"tools/call","params":{
                "name":"honua_create_app_package",
                "arguments":{"templateId":"analysis_dashboard"}
            }}
            """, "app_", "honua://app-packages/");

        var body = await ReadResourceAsync(resourceUri);

        body.GetProperty("packageKind").GetString().Should().Be("app_package");
        body.GetProperty("resourceUri").GetString().Should().Be(resourceUri);
        body.GetProperty("packageStatus").GetString().Should().Be("draft");
        body.GetProperty("deploymentCount").GetInt32().Should().Be(0);
    }

    [IntegrationTest]
    [Operation(Operations.GetById)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    public async Task ResourcesRead_UnknownPackageUri_StillReportsNotFound()
    {
        // The draft fallback must not turn the package resources into a surface that
        // accepts any identifier: an id nobody created is still unknown.
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"map-unknown","method":"resources/read","params":{
                "uri":"honua://map-packages/map_never_created"
            }}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        // Either a JSON-RPC error or an isError result is an acceptable spelling of
        // not-found here; what must not happen is a resolvable package view.
        var isNotFound = root.TryGetProperty("error", out _)
            || (root.TryGetProperty("result", out var result)
                && result.TryGetProperty("isError", out var isError)
                && isError.GetBoolean());
        isNotFound.Should().BeTrue();
    }

    private async Task<string> CreateAndReadUriAsync(string createRequest, string idPrefix, string uriPrefix)
    {
        var response = await PostRpcAsync(createRequest);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = await ReadJsonAsync(response);
        var structured = document.RootElement.GetProperty("result").GetProperty("structuredContent");

        var packageId = structured.GetProperty("packageId").GetString();
        packageId.Should().StartWith(idPrefix);

        var resourceUri = structured.GetProperty("resourceUri").GetString();
        resourceUri.Should().Be(uriPrefix + packageId);
        return resourceUri!;
    }

    private async Task<JsonElement> ReadResourceAsync(string resourceUri)
    {
        var response = await PostRpcAsync(
            $$$"""{"jsonrpc":"2.0","id":"read","method":"resources/read","params":{"uri":"{{{resourceUri}}}"}}""");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;
        root.TryGetProperty("error", out _).Should().BeFalse();

        var contents = root.GetProperty("result").GetProperty("contents");
        contents.GetArrayLength().Should().Be(1);

        var text = contents[0].GetProperty("text").GetString();
        using var body = JsonDocument.Parse(text!);
        return body.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> PostRpcAsync(string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        return await _client.SendAsync(request);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
