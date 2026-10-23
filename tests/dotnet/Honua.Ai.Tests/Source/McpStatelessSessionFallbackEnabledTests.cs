// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Integration tests for the opt-in development convenience
/// <c>Mcp:StatelessSessionFallback=true</c> (honua-server#3027). Outside
/// Development the default is the strict Streamable-HTTP posture (HTTP 404 on
/// an id no instance holds, so the client re-initializes; covered by
/// <c>McpEndpointIntegrationTests</c>). This fixture opts in and proves the
/// fallback still serves a well-formed unknown id session-less, i.e. with the
/// default view and no session echo.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStatelessSessionFallbackEnabledTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _fixture.ConfigureServices(services =>
            services.PostConfigure<McpOptions>(o => o.StatelessSessionFallback = true));
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
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Request_WithUnknownSessionId_WhenFallbackEnabled_IsServedSessionless()
    {
        var response = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId: "deadbeefdeadbeefdeadbeefdeadbeef");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // Only initialize mints: a fallback-served request must not echo or
        // issue a session id.
        response.Headers.TryGetValues("Mcp-Session-Id", out _).Should().BeFalse();

        using var document = await ReadJsonAsync(response);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("tools").ValueKind.Should().Be(JsonValueKind.Array);
        // Session-less means the server default view: no negotiated view is applied.
        result.GetProperty("_meta").GetProperty("view").GetString()
            .Should().Be(McpWorkflowViewCatalog.DefaultViewName);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("DELETE /mcp")]
    public async Task Delete_TerminatesSession_WhenFallbackEnabled_SubsequentPostIsServedSessionless()
    {
        var sessionId = await InitializeAndGetSessionIdAsync();

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        deleteRequest.Headers.Add("Mcp-Session-Id", sessionId);
        var deleteResponse = await _client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDelete = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""",
            sessionId);
        afterDelete.StatusCode.Should().Be(HttpStatusCode.OK);
        afterDelete.Headers.TryGetValues("Mcp-Session-Id", out _).Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task Request_WithMalformedSessionId_WhenFallbackEnabled_Returns404()
    {
        var response = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId: "dead beef");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Request_WithIssuedSessionId_WhenFallbackEnabled_IsAccepted()
    {
        var sessionId = await InitializeAndGetSessionIdAsync();

        var response = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
    }

    private async Task<string> InitializeAndGetSessionIdAsync()
    {
        var response = await PostRpcAsync("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                "protocolVersion":"2025-03-26",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"}
            }}
            """);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Mcp-Session-Id", out var values).Should().BeTrue();
        return values!.Single();
    }

    private async Task<HttpResponseMessage> PostRpcAsync(string body, string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent(body)
        };
        if (sessionId is not null)
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
        }

        return await _client.SendAsync(request);
    }

    private static StringContent JsonContent(string body)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);
        return content;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var stream = await response.Content.ReadAsStreamAsync();
        return await JsonDocument.ParseAsync(stream);
    }
}
