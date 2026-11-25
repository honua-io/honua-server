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
    private const string CapabilitiesParameters = "\"name\":\"honua_list_capabilities\",\"arguments\":{}";
    private const string FullExportParameters = "\"name\":\"honua_list_capabilities\",\"arguments\":{\"fullExport\":true}";

    [IntegrationTheory]
    [InlineData(McpTestTransport.Legacy, "honua.mcp.discover", null)]
    [InlineData(McpTestTransport.Modern, "honua.mcp.discover", null)]
    [InlineData(McpTestTransport.Legacy, "honua.mcp.execute", "insufficient_scope")]
    [InlineData(McpTestTransport.Modern, "honua.mcp.execute", "insufficient_scope")]
    [InlineData(McpTestTransport.Legacy, null, "insufficient_scope")]
    [InlineData(McpTestTransport.Modern, null, "insufficient_scope")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task WorkflowDiscovery_DoesNotGrantInvocationScope(McpTestTransport transport, string? scope, string? denialCode)
    {
        var token = CreateToken(scope);
        await AssertCapabilitiesDiscoveredAsync(token, transport);

        using var response = await SendDiscoveryAsync(
            CapabilitiesParameters, token, transport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        IsToolError(result).Should().Be(denialCode is not null, "discovery cannot widen the token's scopes");
        if (denialCode is null)
        {
            result.GetProperty("structuredContent").GetProperty("serverName").GetString()
                .Should().NotBeNullOrWhiteSpace();
        }
        else
        {
            AssertDenialChannels(result, denialCode);
        }
    }

    private const string CatalogReaderRole = "catalog-reader";

    [IntegrationTheory]
    [InlineData(McpTestTransport.Legacy, "admin", null)]
    [InlineData(McpTestTransport.Modern, "admin", null)]
    [InlineData(McpTestTransport.Legacy, CatalogReaderRole, "permission_denied")]
    [InlineData(McpTestTransport.Modern, CatalogReaderRole, "permission_denied")]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task WorkflowDiscovery_DoesNotGrantFullExportAuthority(McpTestTransport transport, string role, string? denialCode)
    {
        var token = CreateToken("honua.mcp.full", role == CatalogReaderRole ? _catalogReaderRole : role);
        await AssertCapabilitiesDiscoveredAsync(token, transport);

        await AssertOrdinaryCapabilitiesAsync(token, transport);

        using var response = await SendDiscoveryAsync(
            FullExportParameters, token, transport);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        IsToolError(result).Should().Be(denialCode is not null, "the full OAuth scope cannot grant the admin role");
        if (denialCode is null)
        {
            var structured = result.GetProperty("structuredContent");
            structured.GetProperty("toolCount").GetInt32().Should().BeGreaterThan(12);
            structured.GetProperty("toolCount").GetInt32().Should().Be(structured.GetProperty("totalToolCount").GetInt32());
        }
        else
        {
            AssertDenialChannels(result, denialCode);
        }
    }

    private async Task AssertOrdinaryCapabilitiesAsync(string token, McpTestTransport transport)
    {
        using var normalResponse = await SendDiscoveryAsync(
            CapabilitiesParameters, token, transport);
        normalResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var normalDocument = await ReadJsonAsync(normalResponse);
        var normalResult = normalDocument.RootElement.GetProperty("result");
        IsToolError(normalResult).Should().BeFalse("the caller has ordinary catalog discovery access");
        normalResult.GetProperty("structuredContent").GetProperty("serverName").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    private async Task AssertCapabilitiesDiscoveredAsync(string token, McpTestTransport transport)
    {
        using var response = await SendDiscoveryAsync(string.Empty, token, transport);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await ReadJsonAsync(response);
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("_meta").GetProperty("view").GetString().Should().Be("setup");
        result.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).Should().Contain("honua_list_capabilities");
    }

    private async Task<HttpResponseMessage> SendDiscoveryAsync(string parameters, string token, McpTestTransport transport)
    {
        var method = parameters.Length == 0 ? "tools/list" : "tools/call";
        using var request = transport switch
        {
            McpTestTransport.Legacy => CreateDiscoveryRequest(method, parameters, token),
            McpTestTransport.Modern => CreateModernDiscoveryRequest(method, parameters, token),
            _ => throw new ArgumentOutOfRangeException(nameof(transport))
        };
        return await _client.SendAsync(request);
    }

    private static HttpRequestMessage CreateDiscoveryRequest(string method, string parameters, string token)
    {
        var body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method
            + "\",\"params\":{" + parameters + "}}";
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp?view=setup")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static HttpRequestMessage CreateModernDiscoveryRequest(string method, string parameters, string token)
    {
        var modernParameters = parameters.Length == 0
            ? McpTestWire.ModernMetadata
            : parameters + "," + McpTestWire.ModernMetadata;
        var request = CreateDiscoveryRequest(method, modernParameters, token);
        McpTestWire.ConfigureModernHeaders(request, method);
        if (method == "tools/call")
        {
            request.Headers.Add("Mcp-Name", "honua_list_capabilities");
        }

        return request;
    }

    private static void AssertDenialChannels(JsonElement result, string code)
    {
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be(code);
        using var text = JsonDocument.Parse(result.GetProperty("content")[0].GetProperty("text").GetString()!);
        text.RootElement.GetProperty("code").GetString().Should().Be(code);
    }
}
