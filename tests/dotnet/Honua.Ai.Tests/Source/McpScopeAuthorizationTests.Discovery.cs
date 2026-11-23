// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpScopeAuthorizationTests
{
    [IntegrationTheory]
    [InlineData(false, "honua.mcp.discover", true)]
    [InlineData(true, "honua.mcp.discover", true)]
    [InlineData(false, "honua.mcp.execute", false)]
    [InlineData(true, "honua.mcp.execute", false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task WorkflowDiscovery_DoesNotGrantInvocationScope(bool modern, string? scope, bool allowed)
    {
        var token = CreateToken(scope);
        await AssertCapabilitiesDiscoveredAsync(token, modern);

        using var response = await SendDiscoveryAsync(
            "\"name\":\"honua_list_capabilities\",\"arguments\":{}", token, modern);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        IsToolError(result).Should().Be(!allowed, "discovery cannot widen the token's scopes");
        if (allowed)
        {
            result.GetProperty("structuredContent").GetProperty("serverName").GetString()
                .Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            AssertDenialChannels(result, "insufficient_scope");
        }
    }

    [IntegrationTheory]
    [InlineData(false, "admin", true)]
    [InlineData(true, "admin", true)]
    [InlineData(false, ScopeHost.CatalogReader, false)]
    [InlineData(true, ScopeHost.CatalogReader, false)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task WorkflowDiscovery_DoesNotGrantFullExportAuthority(bool modern, string role, bool allowed)
    {
        var token = CreateToken("honua.mcp.full", role);
        await AssertCapabilitiesDiscoveredAsync(token, modern);

        using var normalResponse = await SendDiscoveryAsync(
            "\"name\":\"honua_list_capabilities\",\"arguments\":{}", token, modern);
        normalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var normalDocument = await ReadJsonAsync(normalResponse);
        var normalResult = normalDocument.RootElement.GetProperty("result");
        IsToolError(normalResult).Should().BeFalse("the caller has ordinary catalog discovery access");
        normalResult.GetProperty("structuredContent").GetProperty("serverName").GetString()
            .Should().NotBeNullOrWhiteSpace();

        using var response = await SendDiscoveryAsync(
            "\"name\":\"honua_list_capabilities\",\"arguments\":{\"fullExport\":true}", token, modern);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        IsToolError(result).Should().Be(!allowed, "the full OAuth scope cannot grant the admin role");
        if (allowed)
        {
            var structured = result.GetProperty("structuredContent");
            structured.GetProperty("toolCount").GetInt32().Should().BeGreaterThan(12);
            structured.GetProperty("toolCount").GetInt32().Should().Be(structured.GetProperty("totalToolCount").GetInt32());
        }
        else
        {
            AssertDenialChannels(result, "permission_denied");
        }
    }

    private async Task AssertCapabilitiesDiscoveredAsync(string token, bool modern)
    {
        using var response = await SendDiscoveryAsync(string.Empty, token, modern);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("_meta").GetProperty("view").GetString().Should().Be("setup");
        result.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).Should().Contain("honua_list_capabilities");
    }

    private async Task<HttpResponseMessage> SendDiscoveryAsync(string parameters, string token, bool modern)
    {
        var method = parameters.Length == 0 ? "tools/list" : "tools/call";
        var metadata = modern
            ? "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}"
            : string.Empty;
        var separator = modern && parameters.Length > 0 ? "," : string.Empty;
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method
            + "\",\"params\":{" + parameters + separator + metadata + "}}";
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?view=setup")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (modern)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", method);
            if (method == "tools/call")
            {
                request.Headers.Add("Mcp-Name", "honua_list_capabilities");
            }
        }

        return await _client.SendAsync(request);
    }

    private static void AssertDenialChannels(JsonElement result, string code)
    {
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be(code);
        using var text = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        text.RootElement.GetProperty("code").GetString().Should().Be(code);
    }
}
