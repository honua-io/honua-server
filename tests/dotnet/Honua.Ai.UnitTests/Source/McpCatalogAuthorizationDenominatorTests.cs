// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Denominator proof for #3887. The authorization transition is exercised at the common
/// dispatcher boundary over the exact catalog resolved for the call, including dynamic tools.
/// Adding a new tool cannot evade this test invariant because the loop is built from
/// <see cref="McpDataAccessSurface.GetCatalogEntriesAsync"/>, not a sampled name list.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpCatalogAuthorizationDenominatorTests
{
    [UnitTest]
    [Endpoint("POST /mcp tools/list -> POST /mcp tools/call")]
    public async Task EveryResolvedDescriptor_ReauthorizesAfterPrincipalRemoval_BeforeInvocation()
    {
        var dynamicTool = new InvocationSpyTool("published_mutation");
        var source = new MutableToolSource(dynamicTool);
        var surface = new McpDataAccessSurface(
            McpTaxonomyAlignmentTests.BuildTools(),
            [],
            NullLogger<McpDataAccessSurface>.Instance,
            toolSources: [source]);
        var authorityA = McpTestFactory.AuthenticatedHttpContext();

        var catalog = await surface.GetCatalogEntriesAsync();
        catalog.Should().HaveCountGreaterThan(2);
        catalog.Count(entry => entry.IsDynamic).Should().Be(1);
        catalog.Where(entry => !entry.IsDynamic).Select(entry => entry.Tool.GetType()).Should().BeEquivalentTo(
            McpTaxonomyAlignmentTests.BuildTools().Select(tool => tool.GetType()),
            "the exercised denominator is the roster parity-checked against AddMcpDataAccessSurface registrations");

        foreach (var entry in catalog)
        {
            // Discovery under authority A is deliberately not carried into the call.
            var list = await surface.DispatchAsync(authorityA, Request("tools/list", null), CancellationToken.None);
            list.Should().NotBeNull();

            var authorityRemoved = McpTestFactory.AnonymousHttpContext();
            var response = await surface.DispatchAsync(
                authorityRemoved,
                Request("tools/call", JsonSerializer.Serialize(new { name = entry.Tool.Name, arguments = new { } })),
                CancellationToken.None);

            response.Should().NotBeNull();
            response.Error.Should().BeNull();
            var denied = response.Result!.Value;
            denied.GetProperty("isError").GetBoolean().Should().BeTrue(
                "removed authority must reject every exact-catalog descriptor");
            denied.GetProperty("structuredContent").GetProperty("code").GetString()
                .Should().Be(McpErrorMapper.Codes.Unauthenticated);
        }

        dynamicTool.InvocationCount.Should().Be(0,
            "current call-time authority must be checked before the dynamic invocation seam");
    }

    [UnitTest]
    [Endpoint("POST /mcp tools/call dynamic descriptor revision")]
    public async Task DynamicCatalog_IsResolvedAgainAtCallTime_NotGrantedByPriorMembership()
    {
        var retired = new InvocationSpyTool("published_operation");
        var source = new MutableToolSource(retired);
        var surface = new McpDataAccessSurface([], [], NullLogger<McpDataAccessSurface>.Instance, toolSources: [source]);
        (await surface.GetCatalogEntriesAsync()).Should().ContainSingle();

        source.Tool = null;
        var response = await surface.DispatchAsync(
            McpTestFactory.AuthenticatedHttpContext(),
            Request("tools/call", """{"name":"published_operation","arguments":{}}"""),
            CancellationToken.None);

        response.Should().NotBeNull();
        retired.InvocationCount.Should().Be(0, "a retired published descriptor must not remain callable from discovery state");
    }

    private static McpJsonRpcRequest Request(string method, string? parameters) => new()
    {
        JsonRpc = "2.0",
        Id = McpTestFactory.ParseJson("1"),
        Method = method,
        Params = parameters is null ? null : McpTestFactory.ParseJson(parameters)
    };

    private sealed class MutableToolSource(InvocationSpyTool tool) : IMcpToolSource
    {
        public InvocationSpyTool? Tool { get; set; } = tool;
        public ValueTask<IReadOnlyList<IMcpTool>> GetToolsAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<IMcpTool>>(Tool is null ? [] : [Tool]);
    }

    private sealed class InvocationSpyTool(string name) : IMcpTool
    {
        public int InvocationCount { get; private set; }
        public string Name { get; } = name;
        public string WorkflowFamily => "authorization-denominator";
        public McpToolDescriptor Describe() => new() { Name = Name };
        public Task<McpToolsCallResult> InvokeAsync(
            HttpContext httpContext,
            JsonElement? arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            return Task.FromResult(new McpToolsCallResult());
        }
    }
}
