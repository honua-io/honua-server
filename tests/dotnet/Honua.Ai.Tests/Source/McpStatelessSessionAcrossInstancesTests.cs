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
/// Two in-process server instances with NO shared session store, standing in
/// for AWS Lambda execution environments on a Redis-off cell (honua-release run
/// 38242272260: <c>initialize</c> succeeded, then <c>tools/list</c> and
/// <c>DELETE /mcp</c> answered 404 on another environment). In stateless mode a
/// session minted on A answers the negotiated view on B with no memory shared
/// between them; an id that fails validation still answers 404.
/// </summary>
[Collection("Database")]
[Protocol(TestProtocols.Mcp)]
public sealed class McpStatelessSessionAcrossInstancesTests : IAsyncLifetime
{
    private const string JsonMediaType = "application/json";
    private const string SharedSigningKey = "stateless-session-test-key";

    private readonly WebAppFixture _instanceA = new();
    private readonly WebAppFixture _instanceB = new();

    public async Task InitializeAsync()
    {
        foreach (var instance in new[] { _instanceA, _instanceB })
        {
            instance.ConfigureServices(services =>
                services.PostConfigure<McpOptions>(options =>
                {
                    options.SessionMode = McpSessionModeSetting.Stateless;
                    options.SessionSigningKey = SharedSigningKey;
                }));
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
    public async Task SessionInitializedOnA_ServesTheNegotiatedViewOnB_WithoutASharedStore()
    {
        _instanceA.Services.GetRequiredService<McpSessionManager>().HasSharedStore.Should().BeFalse();
        var (sessionId, sessionMode) = await InitializeWithSetupViewAsync(_instanceA.Client);
        sessionMode.Should().Be("stateless", "initialize states how the issued session is kept");

        var onA = await ListToolsAsync(_instanceA.Client, sessionId);
        var onB = await ListToolsAsync(_instanceB.Client, sessionId);
        var sessionless = await ListToolsAsync(_instanceB.Client, sessionId: null);

        onA.View.Should().Be(McpWorkflowViewCatalog.SetupViewName);
        onB.View.Should().Be(McpWorkflowViewCatalog.SetupViewName, "B must answer the view A negotiated");
        onB.Names.Should().Equal(onA.Names, "tools/list must not change when the request lands on another instance");
        sessionless.View.Should().Be(McpWorkflowViewCatalog.DefaultViewName);
        sessionless.Names.Should().NotEqual(onB.Names);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp")]
    public async Task TamperedSessionId_Returns404_OnEveryInstance()
    {
        var (sessionId, _) = await InitializeWithSetupViewAsync(_instanceA.Client);
        var tampered = sessionId[..^2] + (sessionId[^2] == 'A' ? "B" : "A") + sessionId[^1];

        (await PostRpcAsync(_instanceB.Client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", tampered))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PostRpcAsync(_instanceB.Client, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""", new string('f', 64)))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("DELETE /mcp")]
    public async Task Delete_OnB_Answers405_BecauseAStatelessSessionCannotBeRevoked()
    {
        var (sessionId, _) = await InitializeWithSetupViewAsync(_instanceA.Client);

        using var delete = new HttpRequestMessage(HttpMethod.Delete, "/mcp");
        delete.Headers.Add(McpSessionManager.SessionHeaderName, sessionId);
        var response = await _instanceB.Client.SendAsync(delete);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        response.Content.Headers.Allow.Should().Contain("POST");
        (await ListToolsAsync(_instanceA.Client, sessionId)).View.Should().Be(McpWorkflowViewCatalog.SetupViewName);
    }

    private static async Task<(string SessionId, string? SessionMode)> InitializeWithSetupViewAsync(HttpClient client)
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
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        var mode = document.RootElement.GetProperty("result").TryGetProperty("_meta", out var meta)
            && meta.TryGetProperty("honua.io/session-mode", out var value)
                ? value.GetString()
                : null;
        return (values!.Single(), mode);
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
