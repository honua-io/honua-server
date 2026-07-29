// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Integration tests for the strict Streamable-HTTP session posture with
/// <c>Mcp:StatelessSessionFallback=false</c>. The product default serves a
/// well-formed unknown <c>Mcp-Session-Id</c> statelessly so multi-instance
/// deployments without sticky routing keep working (honua-server#3027); this
/// fixture opts out and proves the pre-#3027 spec behavior — HTTP 404 on any
/// unknown id so the client re-initializes — remains available.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStatelessSessionFallbackDisabledTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Opt out of the stateless fallback for this fixture only; the product
        // default (and every other test) leaves it on per honua-server#3027.
        _fixture.ConfigureServices(services =>
            services.PostConfigure<McpOptions>(o => o.StatelessSessionFallback = false));
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
    public async Task Request_WithUnknownSessionId_WhenFallbackDisabled_Returns404()
    {
        // Strict MCP 2025-03-26 transport behavior: an id the server never
        // issued (or that has expired/been terminated) answers HTTP 404 so the
        // client knows to re-run initialize.
        var response = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            sessionId: "deadbeefdeadbeefdeadbeefdeadbeef");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("DELETE /mcp")]
    public async Task Delete_TerminatesSession_WhenFallbackDisabled_SubsequentUseReturns404()
    {
        var sessionId = await InitializeAndGetSessionIdAsync();

        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        deleteRequest.Headers.Add("Mcp-Session-Id", sessionId);
        var deleteResponse = await _client.SendAsync(deleteRequest);
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // With the fallback off, a terminated session keeps the strict spec
        // 404 on reuse.
        var afterDelete = await PostRpcAsync(
            """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""",
            sessionId);
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task Request_WithIssuedSessionId_WhenFallbackDisabled_IsAccepted()
    {
        // Disabling the fallback must not affect the normal session lifecycle:
        // an id this instance issued is still accepted.
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
