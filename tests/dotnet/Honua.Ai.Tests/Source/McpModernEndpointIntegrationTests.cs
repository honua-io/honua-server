// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.GetMetadata)]
public sealed class McpModernEndpointIntegrationTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "server/discover")]
    public async Task Post_ModernDiscoveryWithoutInitialize_ReturnsCapabilitiesAndNoSession()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "server/discover");
        request.Headers.Add("Mcp-Session-Id", "foreign-session");
        request.Content = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""",
            Encoding.UTF8, "application/json");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Mcp-Session-Id"));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = body.RootElement.GetProperty("result");
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal("2026-07-28", result.GetProperty("supportedVersions")[0].GetString());
        Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.True(result.GetProperty("capabilities").TryGetProperty("resources", out _));
    }

    [IntegrationTheory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    [Endpoint("GET /mcp")]
    [Endpoint("DELETE /mcp")]
    public async Task Request_ModernSessionVerbs_ReturnsMethodNotAllowed(string method)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), "/mcp");
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Session-Id", "foreign-session");

        using var response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Contains("POST", response.Content.Headers.Allow);
    }
}
