// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// Origin and Content-Type checks apply to every protocol version, before the body is read.
public sealed class McpTransportPreconditionTests
{
    private const string ModernBody = """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""";
    private const string LegacyBody = """{"jsonrpc":"2.0","id":1,"method":"ping","params":{}}""";

    [UnitTheory]
    [InlineData("https://unapproved.example", 403)]
    [InlineData("null", 403)]
    [InlineData("https://approved.example", 200)]
    [InlineData(null, 200)]
    public async Task Post_LegacyRequest_ChecksOriginAgainstCorsPolicy(string? origin, int status)
    {
        await using var services = CreateServices();
        var context = CreateLegacyContext(services);
        if (origin is not null)
        {
            context.Request.Headers.Origin = origin;
        }

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(status, context.Response.StatusCode);
    }

    [UnitTheory]
    [InlineData("https://maps.example", "https://maps.example", 200)]
    [InlineData("https://maps.example/honua/", "https://maps.example", 200)]
    [InlineData("https://maps.example:8443", "https://maps.example:8443", 200)]
    [InlineData("https://maps.example", "http://maps.example", 403)]
    [InlineData("https://maps.example", "https://maps.example:8443", 403)]
    [InlineData("https://maps.example", "https://rebound.example", 403)]
    public async Task Post_FromConfiguredPublicOrigin_IsSameOriginWithoutACorsEntry(string publicBaseUrl, string origin, int status)
    {
        await using var services = CreateServices(publicBaseUrl);
        var context = CreateLegacyContext(services);
        context.Request.Host = new HostString(new Uri(origin).Authority);
        context.Request.Headers.Origin = origin;

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(status, context.Response.StatusCode);
    }

    [UnitTheory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task SessionVerb_LegacyRequestFromDisallowedOrigin_IsRefusedBeforeTheHandler(string method)
    {
        await using var services = CreateServices();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = method;
        context.Request.Path = "/mcp";
        context.Request.Headers.Origin = "https://unapproved.example";
        var reached = false;

        var result = await McpModernHttp.ValidateOriginAsync(
            new DefaultEndpointFilterInvocationContext(context),
            _ =>
            {
                reached = true;
                return ValueTask.FromResult<object?>(null);
            });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
    }

    [UnitTheory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("text/plain;charset=UTF-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonp")]
    public async Task Post_NonJsonContentType_Returns415BeforeReadingTheBody(string? contentType)
    {
        foreach (var legacy in new[] { false, true })
        {
            await using var services = CreateServices();
            var context = legacy ? CreateLegacyContext(services) : CreateModernContext(services);
            context.Request.ContentType = contentType;

            await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

            Assert.Equal(StatusCodes.Status415UnsupportedMediaType, context.Response.StatusCode);
            Assert.Equal(0, context.Request.Body.Position);
            Assert.Equal(McpErrorMapper.JsonRpcInvalidRequest, ReadError(context).GetProperty("code").GetInt32());
        }
    }

    [UnitTheory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("APPLICATION/JSON")]
    [InlineData("application/vnd.example+json")]
    public async Task Post_JsonContentType_IsAccepted(string contentType)
    {
        await using var services = CreateServices();
        var context = CreateModernContext(services);
        context.Request.ContentType = contentType;

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static ServiceProvider CreateServices(string? publicBaseUrl = null) => new ServiceCollection()
        .AddLogging()
        .AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = publicBaseUrl })
            .Build())
        .AddCors(options => options.AddPolicy(HttpCorsPolicyNames.Production,
            policy => policy.WithOrigins("https://approved.example")))
        .AddSingleton(Options.Create(new McpOptions()))
        .AddSingleton(_ => new McpSessionManager())
        .AddSingleton(new McpDataAccessSurface(
            Array.Empty<IMcpTool>(), Array.Empty<IMcpResource>(), NullLogger<McpDataAccessSurface>.Instance))
        .BuildServiceProvider();

    private static DefaultHttpContext CreateLegacyContext(IServiceProvider services)
    {
        var context = CreateRequest(services, LegacyBody);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([], "Bearer"));
        return context;
    }

    private static DefaultHttpContext CreateModernContext(IServiceProvider services)
    {
        var context = CreateRequest(services, ModernBody);
        context.Request.Headers["Mcp-Method"] = "server/discover";
        context.Request.Headers["MCP-Protocol-Version"] = "2026-07-28";
        return context;
    }

    private static DefaultHttpContext CreateRequest(IServiceProvider services, string body)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = "POST";
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonElement ReadError(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = JsonDocument.Parse(context.Response.Body);
        return document.RootElement.GetProperty("error").Clone();
    }
}
