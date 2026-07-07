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
/// Integration tests for the optional server-initiated <c>GET /mcp</c> SSE stream
/// with <c>Mcp:ServerInitiatedStreamEnabled=true</c> (opt-in; off by default per
/// #2537). Proves the stream opens for a valid session and — the core #2537
/// regression — that tearing the stream down never invalidates the session, so a
/// spec-compliant SDK client behind a non-streaming ingress keeps working on the
/// same <c>Mcp-Session-Id</c>.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpServerInitiatedStreamTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string EventStreamMediaType = "text/event-stream";

    private readonly WebAppFixture _fixture = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        // Opt into the server-initiated stream for this fixture only; the product
        // default (and every other test) leaves it off so GET /mcp returns 405.
        _fixture.ConfigureServices(services =>
            services.PostConfigure<McpOptions>(o => o.ServerInitiatedStreamEnabled = true));
        await _fixture.InitializeAsync();
        _client = _fixture.Client;
    }

    public async Task DisposeAsync()
    {
        await _fixture.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /mcp")]
    public async Task Get_WhenEnabledWithValidSession_OpensEventStream()
    {
        var sessionId = await InitializeAndGetSessionIdAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStreamMediaType));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var response = await _client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(EventStreamMediaType);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /mcp")]
    public async Task Get_WhenEnabledWithoutValidSession_Returns404()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStreamMediaType));

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /mcp")]
    public async Task Get_WhenEnabledWithoutEventStreamAccept_Returns405()
    {
        var sessionId = await InitializeAndGetSessionIdAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp");
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));

        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task GetStreamTeardown_LeavesSessionUsable_ToolsListStill200()
    {
        // The #2537 regression: initialize → open the standalone GET stream →
        // close it → the same session must still accept POSTs. Before the fix a
        // stream teardown destroyed the session and every later POST 404'd.
        var sessionId = await InitializeAndGetSessionIdAsync();

        using (var streamRequest = new HttpRequestMessage(HttpMethod.Get, "/mcp"))
        {
            streamRequest.Headers.Add("Mcp-Session-Id", sessionId);
            streamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(EventStreamMediaType));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var streamResponse = await _client.SendAsync(
                streamRequest, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            streamResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            // Disposing the response aborts the stream — the client-side teardown
            // a non-streaming ingress inflicts when its idle timeout fires.
        }

        using var postRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")
        };
        postRequest.Headers.Add("Mcp-Session-Id", sessionId);

        using var postResponse = await _client.SendAsync(postRequest);

        postResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(postResponse);
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse(
            "the session must survive the GET stream teardown");
        document.RootElement.GetProperty("result").GetProperty("tools").ValueKind
            .Should().Be(JsonValueKind.Array);
    }

    private async Task<string> InitializeAndGetSessionIdAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent("""
                {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                    "protocolVersion":"2025-03-26",
                    "capabilities":{},
                    "clientInfo":{"name":"honua-tests","version":"1.0.0"}
                }}
                """)
        };
        using var response = await _client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues("Mcp-Session-Id", out var values).Should().BeTrue();
        return values!.Single();
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
