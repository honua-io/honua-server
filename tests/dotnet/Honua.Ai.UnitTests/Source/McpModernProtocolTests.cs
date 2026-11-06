// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp;
using Honua.Geoprocessing;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpModernProtocolTests
{
    [UnitTest]
    public async Task Post_DiscoverWithoutInitialize_ReturnsModernCapabilities()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        var result = response.RootElement.GetProperty("result");
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal("2026-07-28", result.GetProperty("supportedVersions")[0].GetString());
        Assert.Equal("private", result.GetProperty("cacheScope").GetString());
        Assert.Equal(0, result.GetProperty("ttlMs").GetInt32());
        Assert.False(context.Response.Headers.ContainsKey("Mcp-Session-Id"));
    }

    [UnitTheory]
    [InlineData("tools/list")]
    [InlineData("resources/list")]
    [InlineData("resources/templates/list")]
    [InlineData("prompts/list")]
    public async Task Post_CatalogRequest_IgnoresForeignSessionAndAddsCacheHints(string method)
    {
        using var services = CreateServices();
        var context = CreateContext(services, method);
        context.Request.Headers["Mcp-Session-Id"] = "foreign-session";

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(200, context.Response.StatusCode);
        var result = response.RootElement.GetProperty("result");
        Assert.Equal("complete", result.GetProperty("resultType").GetString());
        Assert.Equal(0, result.GetProperty("ttlMs").GetInt32());
        Assert.Equal("private", result.GetProperty("cacheScope").GetString());
    }

    [UnitTheory]
    [InlineData("Mcp-Method")]
    [InlineData("MCP-Protocol-Version")]
    public async Task Post_MissingMirroredHeader_RejectsBeforeDispatch(string header)
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        context.Request.Headers.Remove(header);

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32020, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTest]
    public async Task Post_UnknownModernMethod_Returns404WithMethodNotFound()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "missing/method");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(404, context.Response.StatusCode);
        Assert.Equal(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTheory]
    [InlineData("io.modelcontextprotocol/protocolVersion")]
    [InlineData("io.modelcontextprotocol/clientCapabilities")]
    public async Task Post_MissingRequiredMetadata_ReturnsInvalidParams(string key)
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        ChangeBody(context, body => body["params"]!["_meta"]!.AsObject().Remove(key));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
    }

    [UnitTest]
    public async Task Post_UnsupportedVersion_ReportsPerRequestSupportedVersions()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        context.Request.Headers["MCP-Protocol-Version"] = "2099-01-01";
        ChangeBody(context, body => body["params"]!["_meta"]!["io.modelcontextprotocol/protocolVersion"] = "2099-01-01");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        var error = response.RootElement.GetProperty("error");
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32022, error.GetProperty("code").GetInt32());
        Assert.Equal("2099-01-01", error.GetProperty("data").GetProperty("requested").GetString());
        Assert.Equal("2026-07-28", error.GetProperty("data").GetProperty("supported")[0].GetString());
    }

    [UnitTheory]
    [InlineData("=?base64?5LiW55WM?=", -32602)]
    [InlineData("=?base64?broken?=", -32020)]
    [InlineData("different", -32020)]
    [InlineData("世界", -32020)]
    public async Task Post_EncodedPromptName_ValidatesDecodedValueBeforeDispatch(string header, int code)
    {
        using var services = CreateServices();
        var context = CreateContext(services, "prompts/get");
        context.Request.Headers["Mcp-Name"] = header;
        ChangeBody(context, body => body["params"]!["name"] = "世界");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(code, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTheory]
    [InlineData("https://unapproved.example", 403)]
    [InlineData("null", 403)]
    [InlineData("https://approved.example", 200)]
    public async Task Post_Origin_UsesConfiguredCorsPolicy(string origin, int status)
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        context.Request.Headers.Origin = origin;

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(status, context.Response.StatusCode);
    }

    [UnitTest]
    public async Task Post_RetryMetadata_DoesNotOfferCaching()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "resources/list");
        ChangeBody(context, body => body["params"]!["requestState"] = "retry");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.False(response.RootElement.GetProperty("result").TryGetProperty("ttlMs", out _));
        Assert.False(response.RootElement.GetProperty("result").TryGetProperty("cacheScope", out _));
    }

    [UnitTest]
    public async Task Post_LegacyInitializeWithModernRequestedVersion_PreservesLegacyNegotiation()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "initialize");
        ChangeBody(context, body => body["params"] = new JsonObject
        {
            ["protocolVersion"] = "2026-07-28",
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "legacy", ["version"] = "1" }
        });

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal("2025-06-18", response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.True(context.Response.Headers.ContainsKey("Mcp-Session-Id"));
        Assert.False(response.RootElement.GetProperty("result").TryGetProperty("resultType", out _));
    }

    [UnitTest]
    public async Task Post_AuthenticatedResourceRead_PreservesContentAndAddsPrivateCacheHints()
    {
        var resource = Substitute.For<IMcpResource>();
        resource.Describe().Returns([]);
        resource.DescribeTemplates().Returns([]);
        resource.CanHandle("honua://probe").Returns(true);
        resource.ReadAsync(Arg.Any<HttpContext>(), "honua://probe", Arg.Any<CancellationToken>())
            .Returns(new McpResourcesReadResult
            {
                Contents = [new McpResourceContent { Uri = "honua://probe", Text = "complete content" }]
            });
        using var services = CreateServices(resource);
        var context = CreateContext(services, "resources/read");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "Test"));
        context.Request.Headers["Mcp-Name"] = "honua://probe";
        ChangeBody(context, body => body["params"]!["uri"] = "honua://probe");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        var result = response.RootElement.GetProperty("result");
        Assert.Equal("complete content", result.GetProperty("contents")[0].GetProperty("text").GetString());
        Assert.Equal("private", result.GetProperty("cacheScope").GetString());
        Assert.Equal(0, result.GetProperty("ttlMs").GetInt32());
    }

    [UnitTest]
    public async Task Post_UnknownAuthenticatedResource_ReturnsModernInvalidParams()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "resources/read");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "Test"));
        context.Request.Headers["Mcp-Name"] = "honua://unknown";
        ChangeBody(context, body => body["params"]!["uri"] = "honua://unknown");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTest]
    public async Task Post_ModernBatch_IsRejectedBeforeDispatch()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes($"[{body}]"));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32600, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTest]
    public async Task Post_MalformedModernJson_Returns400AndParseError()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        context.Request.Headers.Accept = "application/json, text/event-stream";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{broken"));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTest]
    public async Task Post_MetadataOnlyMissingVersionWithoutHeader_RejectsMalformedModernRequest()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "tools/list");
        context.Request.Headers.Remove("MCP-Protocol-Version");
        ChangeBody(context, body => body["params"]!["_meta"]!.AsObject().Remove("io.modelcontextprotocol/protocolVersion"));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(400, context.Response.StatusCode);
        Assert.Equal(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [UnitTest]
    public async Task Post_CapabilityToolCall_UsesModernRevisionWithoutCaching()
    {
        var tool = new ListCapabilitiesTool(Substitute.For<IGeoprocessingJobService>(), NullLogger<ListCapabilitiesTool>.Instance);
        using var services = CreateServices([tool], []);
        var context = CreateContext(services, "tools/call");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "Test"));
        context.Request.Headers["Mcp-Name"] = tool.Name;
        ChangeBody(context, body => body["params"]!["name"] = tool.Name);

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        var result = response.RootElement.GetProperty("result");
        Assert.False(result.GetProperty("isError").GetBoolean());
        Assert.Equal("2026-07-28", result.GetProperty("structuredContent").GetProperty("protocolVersions")[0].GetString());
        Assert.False(result.TryGetProperty("ttlMs", out _));
    }

    [UnitTheory]
    [InlineData("{broken")]
    [InlineData("{\"padding\":\"012345678901234567890123456789\"}")]
    public async Task Post_InvalidOrigin_RejectsBeforeReadingMalformedOrOversizedBody(string payload)
    {
        using var services = CreateServices();
        services.GetRequiredService<IOptions<McpOptions>>().Value.MaxRequestBodyBytes = 32;
        var context = CreateContext(services, "server/discover");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        context.Request.Headers.Origin = "https://unapproved.example";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.Equal(0, context.Request.Body.Position);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [UnitTest]
    public async Task Post_ModernPrincipalWithoutDurableActor_Returns401WithPendingRequestId()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "server/discover");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        context.Request.Headers.Origin = "https://approved.example";

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(401, context.Response.StatusCode);
        Assert.Equal(1, response.RootElement.GetProperty("id").GetInt32());
        Assert.True(response.RootElement.GetProperty("error").GetProperty("data").GetProperty("requiresReauthentication").GetBoolean());
    }

    [UnitTest]
    public async Task Post_LegacyPrincipalWithoutDurableActor_PreservesLegacyStatus()
    {
        using var services = CreateServices();
        var context = CreateContext(services, "ping");
        context.Request.Headers.Remove("MCP-Protocol-Version");
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        ChangeBody(context, body => body["params"] = new JsonObject());

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var response = ReadResponse(context);
        Assert.Equal(200, context.Response.StatusCode);
        Assert.Equal(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    private static void ChangeBody(HttpContext context, Action<JsonObject> change)
    {
        context.Request.Body.Position = 0;
        var body = JsonNode.Parse(context.Request.Body)!.AsObject();
        change(body);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body.ToJsonString()));
    }

    private static ServiceProvider CreateServices(params IMcpResource[] resources) => CreateServices([], resources);

    private static ServiceProvider CreateServices(IReadOnlyList<IMcpTool> tools, IReadOnlyList<IMcpResource> resources) => new ServiceCollection()
        .AddLogging()
        .AddCors(options => options.AddPolicy(HttpCorsPolicyNames.Production,
            policy => policy.WithOrigins("https://approved.example")))
        .AddSingleton(Options.Create(new McpOptions()))
        .AddSingleton(new McpSessionManager())
        .AddSingleton(new McpDataAccessSurface(tools, resources, NullLogger<McpDataAccessSurface>.Instance))
        .BuildServiceProvider();

    private static DefaultHttpContext CreateContext(IServiceProvider services, string method)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        var body = """{"jsonrpc":"2.0","id":1,"method":"__METHOD__","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""".Replace("__METHOD__", method, StringComparison.Ordinal);
        context.Request.Method = "POST";
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Headers["Mcp-Method"] = method;
        context.Request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ReadResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }
}
