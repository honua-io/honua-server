// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Core.Configuration;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpRequestTimeoutTests
{
    private const string Call = """{"jsonrpc":"2.0","id":"first","method":"tools/call","params":{"name":"probe"}}""";
    private const string Notification = """{"jsonrpc":"2.0","method":"notifications/initialized"}""";

    [UnitTest]
    public async Task Post_ExpiredPlatformBudget_DoesNotReadBody()
    {
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        var context = new DefaultHttpContext();
        context.Items[TimeoutTokenHelper.LimitsTimeoutTokenKey] = timeout.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None));
        Assert.Equal(0, context.Request.Body.Position);
    }

    [UnitTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Batch_CancelledAfterFirstTool_DoesNotStartNextTool(bool platformBudget)
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var tool = CreateTool(token =>
        {
            calls++;
            cancellation.Cancel();
            Assert.True(token.IsCancellationRequested);
            return Task.FromResult(new McpToolsCallResult { Content = [] });
        });
        using var services = CreateServices(tool);
        var context = CreateContext(services, $"[{Call},{Call}]");
        if (platformBudget)
        {
            context.Items[TimeoutTokenHelper.LimitsTimeoutTokenKey] = cancellation.Token;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            McpEndpointExtensions.HandlePostAsync(context, platformBudget ? CancellationToken.None : cancellation.Token));

        Assert.Equal(1, calls);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [UnitTest]
    public async Task Post_PlatformTimeoutCancelsRunningTool_ReturnsCanonical408()
    {
        var calls = 0;
        var tool = CreateTool(async token =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new McpToolsCallResult { Content = [] };
        });
        using var services = CreateServices(tool);
        var context = CreateContext(services, $"[{Call},{Call}]");
        var limits = new LimitsOptions();
        limits.Connections.RequestTimeout = TimeSpan.FromMilliseconds(500);
        var middleware = new LimitsEnforcementMiddleware(
            ctx => McpEndpointExtensions.HandlePostAsync(ctx, ctx.RequestAborted),
            NullLogger<LimitsEnforcementMiddleware>.Instance, Options.Create(limits));

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, calls);
        Assert.False(context.RequestAborted.IsCancellationRequested);
        Assert.Equal(StatusCodes.Status408RequestTimeout, context.Response.StatusCode);
        Assert.True(context.Response.Body.Length > 0);
    }

    [UnitTest]
    public async Task Batch_WithinBudget_PreservesIdsAndOmitsNotificationResponses()
    {
        using var services = CreateServices(CreateTool(_ => Task.FromResult(new McpToolsCallResult { Content = [] })));
        var context = CreateContext(services, $"[{Notification},{Call},{Call.Replace("\"first\"", "2", StringComparison.Ordinal)}]");
        using var timeout = new CancellationTokenSource();
        context.Items[TimeoutTokenHelper.LimitsTimeoutTokenKey] = timeout.Token;

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(2, document.RootElement.GetArrayLength());
        Assert.Equal("first", document.RootElement[0].GetProperty("id").GetString());
        Assert.Equal(2, document.RootElement[1].GetProperty("id").GetInt32());
        Assert.All(document.RootElement.EnumerateArray(), row => Assert.True(row.TryGetProperty("result", out _)));
    }

    [UnitTest]
    public async Task Batch_OnlyNotifications_Returns202WithoutBody()
    {
        using var services = CreateServices(CreateTool(_ => throw new InvalidOperationException("Notifications must not invoke tools.")));
        var context = CreateContext(services, $"[{Notification},{Notification}]");

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    private static IMcpTool CreateTool(Func<CancellationToken, Task<McpToolsCallResult>> invoke)
    {
        var tool = Substitute.For<IMcpTool>();
        tool.Name.Returns("probe");
        tool.WorkflowFamily.Returns("test");
        tool.InvokeAsync(Arg.Any<HttpContext>(), Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(call => invoke(call.Arg<CancellationToken>()));
        return tool;
    }

    private static ServiceProvider CreateServices(IMcpTool tool) => new ServiceCollection()
        .AddLogging()
        .AddSingleton(Options.Create(new McpOptions()))
        .AddSingleton(new McpSessionManager())
        .AddSingleton(new McpDataAccessSurface([tool], [], NullLogger<McpDataAccessSurface>.Instance))
        .BuildServiceProvider();

    private static DefaultHttpContext CreateContext(IServiceProvider services, string body)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "timeout-test")], "Test"))
        };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/mcp";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }
}
