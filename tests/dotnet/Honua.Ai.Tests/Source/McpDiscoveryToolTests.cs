// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Geoprocessing;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Discovery;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Resources;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the client-agnostic discovery tools (#1949):
/// <c>honua_resolve_entity</c> (NL/text → ranked catalog references) and
/// <c>honua_list_capabilities</c> (self-describing surface manifest). They run
/// through the JSON-RPC dispatcher with a substituted Metadata v2 catalog, so they
/// validate the MCP adapter behavior without a database.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpDiscoveryToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_resolve_entity")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ResolveEntity_RanksMatchingLayerByName()
    {
        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices()),
            ToolCall("resolve-1", ResolveEntityTool.ToolName, """{"text":"parcels"}"""),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("query").GetString().Should().Be("parcels");
        structured.GetProperty("matchCount").GetInt32().Should().BeGreaterThan(0);

        var matches = structured.GetProperty("matches").EnumerateArray().ToArray();
        var top = matches[0];
        top.GetProperty("serviceId").GetString().Should().Be("svc-parcels");
        // The exact-name layer match must outrank the unrelated "Roads" layer.
        matches.Should().Contain(m =>
            m.GetProperty("kind").GetString() == "layer"
            && m.GetProperty("name").GetString()!.Contains("Parcels", StringComparison.OrdinalIgnoreCase));
        matches.Should().NotContain(m =>
            m.GetProperty("name").GetString()!.Contains("Roads", StringComparison.OrdinalIgnoreCase));
    }

    [UnitTest]
    [Operation(Operations.Query)]
    [Endpoint("POST /mcp tools/call honua_resolve_entity")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ResolveEntity_MissingText_ReturnsStructuredError()
    {
        var surface = BuildSurface();
        var response = await surface.DispatchAsync(
            AuthenticatedContext(BuildServices()),
            ToolCall("resolve-2", ResolveEntityTool.ToolName, """{"text":"   "}"""),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue("blank text is an invalid argument");
    }

    [UnitTest]
    [Operation(Operations.GetMetadata)]
    [Endpoint("POST /mcp tools/call honua_list_capabilities")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ListCapabilities_ProjectsLiveToolAndResourceCatalog()
    {
        var surface = BuildSurface();
        var services = BuildServices(surface);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("caps-1", ListCapabilitiesTool.ToolName, "{}"),
            CancellationToken.None);

        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("serverName").GetString().Should().Be("honua.operator.mcp");
        structured.GetProperty("protocolVersions").EnumerateArray()
            .Select(v => v.GetString())
            .Should().Contain("2025-06-18");

        var toolNames = structured.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToArray();
        toolNames.Should().Contain(ListCapabilitiesTool.ToolName);
        toolNames.Should().Contain(ResolveEntityTool.ToolName);
        structured.GetProperty("toolCount").GetInt32().Should().Be(toolNames.Length);

        // Every tool entry carries the LLM-grade metadata a cold client needs.
        foreach (var tool in structured.GetProperty("tools").EnumerateArray())
        {
            tool.GetProperty("description").GetString().Should().NotBeNullOrWhiteSpace();
            tool.TryGetProperty("readOnly", out _).Should().BeTrue();
            tool.GetProperty("workflowFamily").GetString().Should().NotBeNullOrWhiteSpace();
        }

        // The feature catalog grounding resource is surfaced for the client LLM.
        structured.GetProperty("groundingResources").EnumerateArray()
            .Select(u => u.GetString())
            .Should().Contain("honua://catalog/features");
    }

    [UnitTest]
    public void DiscoveryModels_AreResolvableFromSourceGeneratedContext()
    {
        // AOT guard: the discovery DTOs must be source-generated for the explicit
        // serialization the tools perform.
        DiscoveryJsonContext.Default.McpResolveEntityArgument.Should().NotBeNull();
        DiscoveryJsonContext.Default.McpResolveEntityOutput.Should().NotBeNull();
        DiscoveryJsonContext.Default.McpListCapabilitiesArgument.Should().NotBeNull();
        DiscoveryJsonContext.Default.McpListCapabilitiesOutput.Should().NotBeNull();
    }

    private McpOperatorSurface BuildSurface() => new(
        [
            new ResolveEntityTool(_jobService, NullLogger<ResolveEntityTool>.Instance),
            new ListCapabilitiesTool(_jobService, NullLogger<ListCapabilitiesTool>.Instance)
        ],
        [
            new FeatureCatalogResource(_jobService, NullLogger<FeatureCatalogResource>.Instance)
        ],
        NullLogger<McpOperatorSurface>.Instance);

    private static TestMetadataV2GraphProvider BuildGraphProvider() =>
        new TestMetadataV2GraphBuilder()
            .AddResource("res-parcels", "Parcels")
            .AddStorageBinding("bind-parcels", "res-parcels", "public.parcels", storageLayerId: 1)
            .AddResource("res-roads", "Roads")
            .AddStorageBinding("bind-roads", "res-roads", "public.roads", storageLayerId: 2)
            .AddService("svc-parcels", "Parcels")
            .AddPublication("pub-parcels", "svc-parcels", "res-parcels", layerIndex: 0, storageBindingId: "bind-parcels")
            .AddService("svc-roads", "Roads")
            .AddPublication("pub-roads", "svc-roads", "res-roads", layerIndex: 0, storageBindingId: "bind-roads")
            .BuildProvider();

    private static ServiceProvider BuildServices(McpOperatorSurface? surface = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMetadataV2GraphProvider>(BuildGraphProvider());
        if (surface is not null)
        {
            services.AddSingleton(surface);
        }

        return services.BuildServiceProvider();
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""{"name":"{{toolName}}","arguments":{{argumentsJson}}}""")
    };

    private static JsonElement JsonString(string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.Clone();
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
