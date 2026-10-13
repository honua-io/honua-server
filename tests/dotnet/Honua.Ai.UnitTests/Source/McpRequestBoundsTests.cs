// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// SEC-18: <c>POST /mcp</c> bounds the JSON-RPC batch length and the request body
/// size before any element is dispatched. The handler runs directly against a
/// <see cref="DefaultHttpContext"/> with small configured caps, so each test sends
/// the smallest input that crosses a bound and asserts the bounded rejection.
/// </summary>
public sealed class McpRequestBoundsTests
{
    [UnitTest]
    public async Task Batch_AboveConfiguredLength_IsRejectedWholeWithoutDispatch()
    {
        var source = new CountingToolSource();
        var context = CreateContext(source, new McpOptions { MaxBatchSize = 3 }, BatchOf(4));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var document = ReadResponse(context);
        var root = document.RootElement;
        root.ValueKind.Should().Be(JsonValueKind.Object, "an over-length batch is answered by one error object");
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        root.GetProperty("error").GetProperty("message").GetString().Should().Contain("3");
        source.Calls.Should().Be(0, "no batch element is dispatched once the batch exceeds the cap");
    }

    [UnitTest]
    public async Task Batch_AtConfiguredLength_DispatchesEveryElement()
    {
        var source = new CountingToolSource();
        var context = CreateContext(source, new McpOptions { MaxBatchSize = 3 }, BatchOf(3));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        using var document = ReadResponse(context);
        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        document.RootElement.GetArrayLength().Should().Be(3);
        source.Calls.Should().Be(3);
    }

    [UnitTest]
    public void Options_DefaultBatchAndBodyBounds()
    {
        var options = new McpOptions();

        options.MaxBatchSize.Should().Be(50);
        options.MaxRequestBodyBytes.Should().Be(10L * 1024 * 1024);
        options.MaxAnonymousSessions.Should().Be(1_000);
    }

    [UnitTest]
    public async Task Body_AboveConfiguredSize_WithContentLength_Returns413WithoutDispatch()
    {
        var source = new CountingToolSource();
        var body = PaddedToolsList(300);
        var context = CreateContext(source, new McpOptions { MaxRequestBodyBytes = 256 }, body);
        context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        AssertPayloadTooLarge(context);
        source.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task Body_AboveConfiguredSize_WithoutContentLength_Returns413WithoutDispatch()
    {
        var source = new CountingToolSource();
        var context = CreateContext(source, new McpOptions { MaxRequestBodyBytes = 256 }, PaddedToolsList(300));
        context.Request.ContentLength = null;

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        AssertPayloadTooLarge(context);
        source.Calls.Should().Be(0);
    }

    [UnitTest]
    public async Task Body_WithinConfiguredSize_IsDispatched()
    {
        var source = new CountingToolSource();
        var context = CreateContext(source, new McpOptions { MaxRequestBodyBytes = 256 }, PaddedToolsList(200));

        await McpEndpointExtensions.HandlePostAsync(context, CancellationToken.None);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        using var document = ReadResponse(context);
        document.RootElement.GetProperty("id").GetInt32().Should().Be(1);
        source.Calls.Should().Be(1);
    }

    private static void AssertPayloadTooLarge(HttpContext context)
    {
        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        using var document = ReadResponse(context);
        var root = document.RootElement;
        root.GetProperty("id").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32600);
        root.GetProperty("error").GetProperty("message").GetString().Should().Contain("256");
    }

    private static string BatchOf(int count) =>
        "[" + string.Join(',', Enumerable.Range(1, count)
            .Select(i => $$"""{"jsonrpc":"2.0","id":{{i}},"method":"tools/list"}""")) + "]";

    /// <summary>
    /// A single <c>tools/list</c> request padded with an ignored parameter to exactly
    /// <paramref name="totalBytes"/> bytes.
    /// </summary>
    private static string PaddedToolsList(int totalBytes)
    {
        const string prefix = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\",\"params\":{\"pad\":\"";
        const string suffix = "\"}}";
        var padding = totalBytes - prefix.Length - suffix.Length;
        return prefix + new string('x', padding) + suffix;
    }

    private static DefaultHttpContext CreateContext(CountingToolSource source, McpOptions options, string body)
    {
        var services = new ServiceCollection()
            .AddSingleton(new McpDataAccessSurface(
                [],
                [],
                NullLogger<McpDataAccessSurface>.Instance,
                limits: null,
                toolSources: [source]))
            .AddSingleton(new McpSessionManager())
            .AddSingleton<ILogger<McpDataAccessSurface>>(NullLogger<McpDataAccessSurface>.Instance)
            .AddSingleton(Options.Create(options))
            .BuildServiceProvider();

        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = McpEndpointExtensions.RoutePath;
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static JsonDocument ReadResponse(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body);
    }

    private sealed class CountingToolSource : IMcpToolSource
    {
        public int Calls { get; private set; }

        public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult<IReadOnlyList<IMcpTool>>([]);
        }
    }
}
