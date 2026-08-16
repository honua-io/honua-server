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
