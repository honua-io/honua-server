// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed class McpCancellationWireTests
{
    [UnitTest]
    public async Task ActualPostDispatcher_ValidCancellationReachesHandler_WithoutAbortingOriginalRequest()
    {
        var source = new BlockedCatalog();
        var manager = new McpSessionManager();
        manager.TryCreateSession(McpSessionManager.AnonymousPrincipalKey, false, out var session);
        await using var services = new ServiceCollection()
            .AddSingleton(new McpDataAccessSurface([], [], NullLogger<McpDataAccessSurface>.Instance,
                toolSources: [source]))
            .AddSingleton(manager)
            .AddSingleton<ILogger<McpDataAccessSurface>>(NullLogger<McpDataAccessSurface>.Instance)
            .AddSingleton(Options.Create(new McpOptions()))
            .BuildServiceProvider();

        var original = Context("""{"jsonrpc":"2.0","id":7,"method":"tools/list"}""");
        var running = McpEndpointExtensions.HandlePostAsync(original, CancellationToken.None);
        try
        {
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(running.IsCompleted);

            // Id-bearing notifications stay malformed; invalid target ids and
            // forward-compatible unknown notifications stay harmless.
            var malformed = Context("""{"jsonrpc":"2.0","id":99,"method":"notifications/cancelled","params":{"requestId":7}}""");
            await McpEndpointExtensions.HandlePostAsync(malformed, CancellationToken.None);
            using var error = Read(malformed);
            Assert.Equal(-32600, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            foreach (var body in new[]
                     {
                         """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":true}}""",
                         """{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"7"}}""",
                         """{"jsonrpc":"2.0","method":"notifications/future","params":{"requestId":7}}"""
                     })
            {
                var ignored = Context(body);
                await McpEndpointExtensions.HandlePostAsync(ignored, CancellationToken.None);
                Assert.Equal(StatusCodes.Status202Accepted, ignored.Response.StatusCode);
                Assert.Equal(0, ignored.Response.Body.Length);
                Assert.False(source.Stopped.Task.IsCompleted);
            }

            var cancel = Context("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}""");
            await McpEndpointExtensions.HandlePostAsync(cancel, CancellationToken.None);
            Assert.Equal(StatusCodes.Status202Accepted, cancel.Response.StatusCode);
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(source.Stopped.Task.IsCompletedSuccessfully);
            Assert.False(original.RequestAborted.IsCancellationRequested);
            using var response = Read(original);
            Assert.Equal(7, response.RootElement.GetProperty("id").GetInt32());
            Assert.Equal(-32800, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
            Assert.False(response.RootElement.TryGetProperty("result", out _));
        }
        finally
        {
            source.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(3));
        }

        DefaultHttpContext Context(string body)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = "/mcp";
            context.Request.ContentType = "application/json";
            context.Request.Headers[McpSessionManager.SessionHeaderName] = session;
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
            context.Response.Body = new MemoryStream();
            return context;
        }
    }

    private static JsonDocument Read(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }

    private sealed class BlockedCatalog : IMcpToolSource
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
                return [];
            }
            finally
            {
                Stopped.TrySetResult();
            }
        }
    }
}
