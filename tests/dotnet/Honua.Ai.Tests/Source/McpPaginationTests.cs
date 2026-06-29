// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Pins the MCP opaque-cursor pagination contract (#1953): list paging over
/// <c>tools/list</c> / <c>resources/list</c> / <c>resources/templates/list</c> /
/// <c>prompts/list</c>, the windowed chunking of large <c>resources/read</c>
/// documents, and the dispatcher plumbing that surfaces <c>nextCursor</c> and
/// rejects an invalid cursor with JSON-RPC <c>-32602</c> invalid-params.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpPaginationTests
{
    [UnitTest]
    public void Page_WhenMoreRemain_ReturnsFirstPageAndOpaqueNextCursor()
    {
        var items = Enumerable.Range(0, 10).ToList();

        var page = McpPagination.Page(items, cursor: null, pageSize: 4, out var nextCursor);

        page.Should().Equal(0, 1, 2, 3);
        nextCursor.Should().NotBeNullOrWhiteSpace();
        // Cursors are opaque tokens, not the raw offset.
        nextCursor.Should().NotBe("4");
    }

    [UnitTest]
    public void Page_SecondCall_ResumesFromCursor()
    {
        var items = Enumerable.Range(0, 10).ToList();

        var first = McpPagination.Page(items, cursor: null, pageSize: 4, out var firstCursor);
        var second = McpPagination.Page(items, firstCursor, pageSize: 4, out var secondCursor);
        var third = McpPagination.Page(items, secondCursor, pageSize: 4, out var thirdCursor);

        first.Should().Equal(0, 1, 2, 3);
        second.Should().Equal(4, 5, 6, 7);
        third.Should().Equal(8, 9);
        // The final page omits the cursor.
        thirdCursor.Should().BeNull();
    }

    [UnitTest]
    public void Page_WhenAllFitInOnePage_OmitsNextCursor()
    {
        var items = Enumerable.Range(0, 3).ToList();

        var page = McpPagination.Page(items, cursor: null, pageSize: 50, out var nextCursor);

        page.Should().Equal(0, 1, 2);
        nextCursor.Should().BeNull();
    }

    [UnitTest]
    public void Page_WithMalformedCursor_ThrowsValidation()
    {
        var items = Enumerable.Range(0, 10).ToList();

        var act = () => McpPagination.Page(items, cursor: "not-a-real-cursor", pageSize: 4, out _);

        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    public void Chunk_WhenDocumentFitsBudget_ReturnsVerbatimWithoutCursor()
    {
        IReadOnlyList<McpResourceContent> contents =
        [
            new McpResourceContent { Uri = "honua://catalog/x", MimeType = "application/json", Text = "{\"a\":1}" }
        ];

        var page = McpPagination.Chunk(contents, cursor: null, maxChars: 1000, out var nextCursor);

        page.Should().BeSameAs(contents);
        nextCursor.Should().BeNull();
    }

    [UnitTest]
    public void Chunk_WhenDocumentExceedsBudget_SplitsAndReassembles()
    {
        var text = new string('x', 25) + new string('y', 25);
        IReadOnlyList<McpResourceContent> contents =
        [
            new McpResourceContent { Uri = "honua://jobs/j/results", MimeType = "application/json", Text = text }
        ];

        var reassembled = string.Empty;
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = McpPagination.Chunk(contents, cursor, maxChars: 20, out cursor);
            page.Should().HaveCount(1);
            page[0].Uri.Should().Be("honua://jobs/j/results");
            page[0].Text.Length.Should().BeLessThanOrEqualTo(20);
            reassembled += page[0].Text;
            pages++;
        }
        while (cursor is not null && pages < 10);

        pages.Should().Be(3);
        reassembled.Should().Be(text);
    }

    [UnitTest]
    public void Chunk_WithMalformedCursor_ThrowsValidation()
    {
        IReadOnlyList<McpResourceContent> contents =
        [
            new McpResourceContent { Uri = "honua://catalog/x", MimeType = "application/json", Text = "abc" }
        ];

        var act = () => McpPagination.Chunk(contents, cursor: "%%bogus%%", maxChars: 1, out _);

        act.Should().Throw<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task ToolsList_OverDispatcher_PagesWithNextCursor()
    {
        var surface = new McpOperatorSurface(
            tools: Enumerable.Range(0, 5).Select(i => (IMcpTool)new StubTool($"honua_tool_{i}")),
            resources: [],
            logger: NullLogger<McpOperatorSurface>.Instance,
            limits: new McpSurfaceLimits(ListPageSize: 2, MaxResourceReadChars: 1000));

        var firstNames = await ListToolPageAsync(surface, cursor: null);
        firstNames.Names.Should().Equal("honua_tool_0", "honua_tool_1");
        firstNames.NextCursor.Should().NotBeNullOrWhiteSpace();

        var secondNames = await ListToolPageAsync(surface, firstNames.NextCursor);
        secondNames.Names.Should().Equal("honua_tool_2", "honua_tool_3");

        var thirdNames = await ListToolPageAsync(surface, secondNames.NextCursor);
        thirdNames.Names.Should().Equal("honua_tool_4");
        thirdNames.NextCursor.Should().BeNull();
    }

    [UnitTest]
    public async Task ToolsList_WithInvalidCursor_ReturnsInvalidParams()
    {
        var surface = new McpOperatorSurface(
            tools: [new StubTool("honua_tool_0")],
            resources: [],
            logger: NullLogger<McpOperatorSurface>.Instance,
            limits: new McpSurfaceLimits(ListPageSize: 2, MaxResourceReadChars: 1000));

        var response = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"x","method":"tools/list","params":{"cursor":"garbage"}}""");

        response!.Error.Should().NotBeNull();
        response.Error!.Code.Should().Be(McpErrorMapper.JsonRpcInvalidParams);
        response.Error.Data!.Code.Should().Be(McpErrorMapper.Codes.InvalidArgument);
    }

    [UnitTest]
    public async Task ResourcesRead_OverDispatcher_ChunksLargeDocument()
    {
        var text = new string('z', 50);
        var surface = new McpOperatorSurface(
            tools: [],
            resources: [new StubResource("honua://big/doc", text)],
            logger: NullLogger<McpOperatorSurface>.Instance,
            limits: new McpSurfaceLimits(ListPageSize: 50, MaxResourceReadChars: 20));

        var context = McpTestFactory.AuthenticatedHttpContext();

        var first = await DispatchAsync(
            surface,
            """{"jsonrpc":"2.0","id":"r1","method":"resources/read","params":{"uri":"honua://big/doc"}}""",
            context);

        var firstResult = first!.Result!.Value;
        var firstChunk = firstResult.GetProperty("contents")[0].GetProperty("text").GetString();
        firstChunk!.Length.Should().Be(20);
        var nextCursor = firstResult.GetProperty("nextCursor").GetString();
        nextCursor.Should().NotBeNullOrWhiteSpace();

        var second = await DispatchAsync(
            surface,
            "{\"jsonrpc\":\"2.0\",\"id\":\"r2\",\"method\":\"resources/read\",\"params\":{\"uri\":\"honua://big/doc\",\"cursor\":\""
                + nextCursor + "\"}}",
            context);
        var secondResult = second!.Result!.Value;
        secondResult.GetProperty("contents")[0].GetProperty("text").GetString()!.Length.Should().Be(20);
    }

    private static async Task<(string[] Names, string? NextCursor)> ListToolPageAsync(
        McpOperatorSurface surface,
        string? cursor)
    {
        var paramsJson = cursor is null
            ? string.Empty
            : ",\"params\":{\"cursor\":\"" + cursor + "\"}";
        var response = await DispatchAsync(
            surface,
            "{\"jsonrpc\":\"2.0\",\"id\":\"t\",\"method\":\"tools/list\"" + paramsJson + "}");

        var result = response!.Result!.Value;
        var names = result.GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToArray();
        var nextCursor = result.TryGetProperty("nextCursor", out var nc) ? nc.GetString() : null;
        return (names, nextCursor);
    }

    private static async Task<McpJsonRpcResponse?> DispatchAsync(
        McpOperatorSurface surface,
        string body,
        HttpContext? context = null)
    {
        var request = JsonSerializer.Deserialize(body, McpJsonContext.Default.McpJsonRpcRequest)!;
        return await surface.DispatchAsync(
            context ?? McpTestFactory.AuthenticatedHttpContext(),
            request,
            CancellationToken.None);
    }

    private sealed class StubTool : IMcpTool
    {
        private readonly string _name;

        public StubTool(string name) => _name = name;

        public string Name => _name;

        public string WorkflowFamily => McpTelemetry.WorkflowFamily.Unknown;

        public McpToolDescriptor Describe() => new()
        {
            Name = _name,
            Description = "stub",
            InputSchema = McpTestFactory.ParseJson("{\"type\":\"object\"}")
        };

        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext,
            JsonElement? arguments,
            CancellationToken cancellationToken) =>
            Task.FromResult(new McpToolsCallResult());
    }

    private sealed class StubResource : IMcpResource
    {
        private readonly string _uri;
        private readonly string _text;

        public StubResource(string uri, string text)
        {
            _uri = uri;
            _text = text;
        }

        public string Family => McpTelemetry.ResourceFamily.Unknown;

        public IReadOnlyList<McpResourceDescriptor> Describe() =>
            [new McpResourceDescriptor { Uri = _uri, Name = "stub", Description = "stub" }];

        public IReadOnlyList<McpResourceTemplateDescriptor> DescribeTemplates() => [];

        public bool CanHandle(string uri) => string.Equals(uri, _uri, System.StringComparison.Ordinal);

        public Task<McpResourcesReadResult> ReadAsync(
            HttpContext httpContext,
            string uri,
            CancellationToken cancellationToken) =>
            Task.FromResult(new McpResourcesReadResult
            {
                Contents =
                [
                    new McpResourceContent { Uri = _uri, MimeType = "application/json", Text = _text }
                ]
            });
    }
}
