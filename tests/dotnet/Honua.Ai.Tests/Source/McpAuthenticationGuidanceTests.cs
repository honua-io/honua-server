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
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
[Operation(Operations.Security)]
public sealed class McpAuthenticationGuidanceTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureWebHost(builder =>
    {
        builder.UseEnvironment("Test");
        builder.UseSetting("HONUA_DEV_AUTH", "false");
        builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
    });

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("default", false)]
    [InlineData("configure", false)]
    [InlineData("setup", false)]
    [InlineData("review", false)]
    [InlineData(null, false)]
    [InlineData("default", true)]
    [InlineData("configure", true)]
    [InlineData("setup", true)]
    [InlineData("review", true)]
    [InlineData(null, true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_AnonymousView_DoesNotAdvertiseProtectedTools(string? view, bool modern)
    {
        using var anonymous = _fixture.CreateClient();
        using var request = BuildRequest("tools/list", "{}", view, modern);
        using var response = await anonymous.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = document.RootElement.GetProperty("result");
        result.GetProperty("tools").GetArrayLength().Should().Be(0);
        var metadata = result.GetProperty("_meta");
        metadata.GetProperty("view").GetString().Should().Be(view ?? "default");
        metadata.GetProperty("toolCount").GetInt32().Should().Be(0);
        metadata.GetProperty("stages").GetArrayLength().Should().BeGreaterThan(0);
        foreach (var stage in metadata.GetProperty("stages").EnumerateArray())
        {
            stage.GetProperty("tools").GetArrayLength().Should().Be(0);
        }

        using var authenticated = _fixture.CreateAdminClient();
        using var allowed = BuildRequest("tools/list", "{}", view, modern);
        using var allowedResponse = await authenticated.SendAsync(allowed);
        allowedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        using var allowedDocument = JsonDocument.Parse(await allowedResponse.Content.ReadAsStringAsync());
        allowedDocument.RootElement.GetProperty("result").GetProperty("tools")
            .GetArrayLength().Should().BeGreaterThan(0);
        var authenticatedMetadata = allowedDocument.RootElement.GetProperty("result").GetProperty("_meta");
        authenticatedMetadata.GetProperty("title").GetString().Should().Be(metadata.GetProperty("title").GetString());
        authenticatedMetadata.GetProperty("revisionDigest").GetString().Should().Be(metadata.GetProperty("revisionDigest").GetString());
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_AnonymousFullExport_ReturnsPermissionDenied(bool modern)
    {
        using var client = _fixture.CreateClient();
        using var request = BuildRequest("tools/list", "{}", "full", modern);
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(modern ? HttpStatusCode.BadRequest : HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("error").GetProperty("data").GetProperty("code")
            .GetString().Should().Be("permission_denied");
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
            using var request = BuildRequest("tools/call", "{\"name\":\"" + tool + "\",\"arguments\":{}}", "configure", modern);
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

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "resources/read")]
    public async Task ResourcesRead_AnonymousKnownOrUnknownUri_ReturnsSameCredentialGuidance(bool modern)
    {
        using var client = _fixture.CreateClient();
        string? first = null;
        foreach (var uri in new[] { "honua://catalog/processes", "honua://unknown/thing" })
        {
            using var request = BuildRequest("resources/read", "{\"uri\":\"" + uri + "\"}", "default", modern);
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(modern ? HttpStatusCode.Unauthorized : HttpStatusCode.OK);
            var responseBody = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(responseBody);
            var message = document.RootElement.GetProperty("error").GetProperty("message").GetString()!;
            message.Should().Contain("X-API-Key").And.Contain("administrator")
                .And.Contain("Authorization: Bearer").And.Contain("identity provider");
            message.Should().NotContain(uri);
            first ??= responseBody;
            responseBody.Should().Be(first);
        }
    }

    private static HttpRequestMessage BuildRequest(string method, string parameters, string? view, bool modern)
    {
        var metadata = modern
            ? "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\",\"io.modelcontextprotocol/clientCapabilities\":{}}"
            : string.Empty;
        var separator = modern && parameters.Length > 2 ? "," : string.Empty;
        var request = new HttpRequestMessage(HttpMethod.Post, view is null ? "/mcp" : "/mcp?view=" + view)
        {
            Content = new StringContent(
                "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"" + method
                    + "\",\"params\":{" + parameters[1..^1] + separator + metadata + "}}",
                Encoding.UTF8, "application/json")
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (modern)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", method);
            using var arguments = JsonDocument.Parse(parameters);
            if (arguments.RootElement.TryGetProperty(method == "resources/read" ? "uri" : "name", out var name))
            {
                request.Headers.Add("Mcp-Name", name.GetString());
            }
        }

        return request;
    }
}
