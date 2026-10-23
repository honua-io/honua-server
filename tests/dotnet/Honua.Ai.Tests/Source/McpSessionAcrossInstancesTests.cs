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
/// Two in-process server instances sharing one session store, standing in for
/// replicas behind a non-sticky router (AWS Lambda; honua-release run
/// 38099175519, journey stage 1.5, diagnosed in honua-release#531). A session
/// negotiated on instance A, including its workflow view, must be honoured on
/// instance B, or refused with HTTP 404, never silently downgraded to the
/// session-less default view.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpSessionAcrossInstancesTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";

    private readonly McpSessionSharedStoreTests.FakeSharedSessionStore _sharedStore = new(TimeProvider.System);
    private readonly WebAppFixture _instanceA = new();
    private readonly WebAppFixture _instanceB = new();

    public async Task InitializeAsync()
    {
        foreach (var instance in new[] { _instanceA, _instanceB })
        {
            instance.ConfigureServices(services =>
                services.AddSingleton<IMcpSessionStore>(_sharedStore));
            await instance.InitializeAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await _instanceA.DisposeAsync();
        await _instanceB.DisposeAsync();
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task SessionInitializedOnA_ServesTheSameNegotiatedViewOnB()
    {
        var sessionId = await InitializeWithSetupViewAsync(_instanceA.Client);

        var onA = await ListToolsAsync(_instanceA.Client, sessionId);
        var onB = await ListToolsAsync(_instanceB.Client, sessionId);
        var sessionless = await ListToolsAsync(_instanceB.Client, sessionId: null);

        onA.View.Should().Be(McpWorkflowViewCatalog.SetupViewName);
        onB.View.Should().Be(McpWorkflowViewCatalog.SetupViewName, "B must honour the view A negotiated");
        onB.Names.Should().Equal(onA.Names, "tools/list must not change when the request lands on another instance");
        sessionless.View.Should().Be(McpWorkflowViewCatalog.DefaultViewName);
        sessionless.Names.Should().NotEqual(onB.Names, "the regression served B's request with the session-less default view");
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("DELETE /mcp")]
    public async Task SessionDeletedOnB_Returns404OnA()
    {
        var sessionId = await InitializeWithSetupViewAsync(_instanceA.Client);
        (await PostRpcAsync(_instanceB.Client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", sessionId))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Add(McpSessionManager.SessionHeaderName, sessionId);
        (await _instanceB.Client.SendAsync(delete)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await PostRpcAsync(_instanceA.Client, """{"jsonrpc":"2.0","id":3,"method":"tools/list"}""", sessionId))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task IdNoInstanceHolds_Returns404_SoTheClientReinitializes()
    {
        var response = await PostRpcAsync(
            _instanceB.Client,
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            new string('f', 64));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static async Task<string> InitializeWithSetupViewAsync(HttpClient client)
    {
        var response = await PostRpcAsync(client, """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{
                "protocolVersion":"2025-06-18",
                "capabilities":{},
                "clientInfo":{"name":"honua-tests","version":"1.0.0"},
                "_meta":{"honua.io/workflow-view":"setup"}
            }}
            """);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.TryGetValues(McpSessionManager.SessionHeaderName, out var values).Should().BeTrue();
        return values!.Single();
    }

    private static async Task<(string? View, string[] Names)> ListToolsAsync(HttpClient client, string? sessionId)
    {
        var response = await PostRpcAsync(client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", sessionId);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var result = document.RootElement.GetProperty("result");
        var names = result.GetProperty("tools").EnumerateArray()
            .Select(static tool => tool.GetProperty("name").GetString()!)
            .ToArray();
        var view = result.TryGetProperty("_meta", out var meta) && meta.TryGetProperty("view", out var v)
            ? v.GetString()
            : null;
        return (view, names);
    }

    private static async Task<HttpResponseMessage> PostRpcAsync(HttpClient client, string body, string? sessionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(JsonMediaType);
        if (sessionId is not null)
        {
            request.Headers.Add(McpSessionManager.SessionHeaderName, sessionId);
        }

        return await client.SendAsync(request);
    }
}
