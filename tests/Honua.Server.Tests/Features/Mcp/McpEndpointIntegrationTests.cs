// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Mcp;

/// <summary>
/// Integration tests for the MCP operator surface exercised through the full
/// ASP.NET Core pipeline. Covers JSON-RPC framing, method dispatch, and
/// error mapping so the public <c>POST /mcp</c> contract is validated end
/// to end, not just at the handler level.
/// </summary>
[Collection("Database")]
[Protocol(Protocols.Mcp)]
public sealed class McpEndpointIntegrationTests : IAsyncLifetime
{
    private const string McpRoute = "/mcp";
    private const string JsonMediaType = "application/json";

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
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Initialize_ReturnsJsonRpcResultWithServerInfo()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(JsonMediaType);

        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("jsonrpc").GetString().Should().Be("2.0");
        root.GetProperty("id").GetInt32().Should().Be(1);
        // The context omits null properties, so the absent "error" field signals success.
        root.TryGetProperty("error", out _).Should().BeFalse();

        var result = root.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().Should().NotBeNullOrEmpty();
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("honua.operator.mcp");
        result.GetProperty("capabilities").GetProperty("tools").Should().NotBeNull();
        result.GetProperty("capabilities").GetProperty("resources").Should().NotBeNull();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task UnknownMethod_ReturnsStructuredJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"probe-1","method":"nonexistent/method"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("probe-1");
        var error = root.GetProperty("error");
        error.GetProperty("message").GetString().Should().Contain("nonexistent/method");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("not_found");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task MalformedJson_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("{not-json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ToolsCall_WithMalformedParams_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"call-bad","method":"tools/call","params":["not","an","object"]}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("call-bad");
        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task ResourcesRead_WithMalformedParams_ReturnsInvalidArgumentJsonRpcError()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":"read-bad","method":"resources/read","params":"not-an-object"}
            """);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var root = document.RootElement;

        root.GetProperty("id").GetString().Should().Be("read-bad");
        var error = root.GetProperty("error");
        error.GetProperty("data").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    private async Task<HttpResponseMessage> PostRpcAsync(string body)
    {
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);
        return await _client.PostAsync(McpRoute, content);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
