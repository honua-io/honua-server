// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpAuthenticationGuidanceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("default", false)]
    [InlineData("configure", false)]
    [InlineData("setup", false)]
    [InlineData("review", false)]
    [InlineData("default", true)]
    [InlineData("configure", true)]
    [InlineData("setup", true)]
    [InlineData("review", true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_AnonymousView_DoesNotAdvertiseProtectedTools(string view, bool modern)
    {
        using var anonymous = _fixture.CreateClient();
        using var request = BuildRequest("tools/list", "{}", view, modern);
        using var response = await anonymous.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("tools").GetArrayLength().Should().Be(0);

        using var authenticated = _fixture.CreateAdminClient();
        using var allowed = BuildRequest("tools/list", "{}", view, modern);
        using var allowedResponse = await authenticated.SendAsync(allowed);
        allowedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var allowedDocument = JsonDocument.Parse(await allowedResponse.Content.ReadAsStringAsync());
        allowedDocument.RootElement.GetProperty("result").GetProperty("tools")
            .GetArrayLength().Should().BeGreaterThan(0);
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_AnonymousKnownOrUnknownTool_ReturnsSameCredentialGuidance(bool modern)
    {
        using var client = _fixture.CreateClient();
        string? first = null;
        foreach (var tool in new[] { "honua_ingest_dataset", "unknown_auth_probe" })
        {
            using var request = BuildRequest("tools/call", $$"""{"name":"{{tool}}","arguments":{}}""", "configure", modern);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var result = document.RootElement.GetProperty("result");
            result.GetProperty("isError").GetBoolean().Should().BeTrue();
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            text.Should().Contain("X-API-Key").And.Contain("administrator")
                .And.Contain("Authorization: Bearer").And.Contain("identity provider");
            text.Should().NotContain(tool);
            first ??= text;
            text.Should().Be(first);
        }
    }

    private static HttpRequestMessage BuildRequest(string method, string parameters, string view, bool modern)
    {
        var metadata = modern
            ? "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}"
            : string.Empty;
        var separator = modern && parameters.Length > 2 ? "," : string.Empty;
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?view=" + view)
        {
            Content = new StringContent(
                $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{ {{parameters[1..^1]}}{{separator}}{{metadata}} }}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (modern)
        {
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        }

        return request;
    }
}
