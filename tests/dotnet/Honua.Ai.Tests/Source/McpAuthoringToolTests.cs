// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Geoprocessing;
using Honua.Ai.AppGeneration;
using Honua.Ai.MapGeneration;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// Focused coverage for the authoring/publishing MCP tools (#1951):
/// <c>honua_create_map_package</c> and <c>honua_create_app_package</c>. These run
/// through the JSON-RPC dispatcher while substituting the canonical
/// map/app-generation services, so they validate the MCP adapter behavior
/// (argument mapping, prompt composition, result projection, capability gating)
/// without a configured workflow-generation provider.
/// </summary>
[Protocol(TestProtocols.Mcp)]
public sealed class McpAuthoringToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_IncludesAuthoringTools()
    {
        var surface = new McpOperatorSurface(
            [
                new CreateMapPackageTool(_jobService, NullLogger<CreateMapPackageTool>.Instance),
                new CreateAppPackageTool(_jobService, NullLogger<CreateAppPackageTool>.Instance),
            ],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(new ServiceCollection().BuildServiceProvider()),
            ListToolsRequest("list-1"),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var names = response.Result!.Value.GetProperty("tools")
            .EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToArray();

        names.Should().Contain([CreateMapPackageTool.ToolName, CreateAppPackageTool.ToolName]);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_DelegatesToGenerationServiceAndProjectsResult()
    {
        var mapGen = Substitute.For<IMapGenerationService>();
        mapGen.GenerateAsync(Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MapGenerationResult
            {
                Status = "generated",
                Rationale = "Composed a parcels map.",
                Provider = "anthropic",
                Model = "claude"
            });

        var services = new ServiceCollection()
            .AddSingleton(mapGen)
            .BuildServiceProvider();

        var surface = new McpOperatorSurface(
            [new CreateMapPackageTool(_jobService, NullLogger<CreateMapPackageTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("map-1", CreateMapPackageTool.ToolName,
                """{"prompt":"a map of parcels","styleId":"style_blue","templateId":"analysis_default"}"""),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("generated");
        structured.GetProperty("provider").GetString().Should().Be("anthropic");

        // The standard composition selectors are woven into the prompt the
        // canonical generation pipeline receives.
        await mapGen.Received(1).GenerateAsync(
            Arg.Is<MapGenerationRequest>(r =>
                r.Prompt.Contains("a map of parcels")
                && r.Prompt.Contains("style_blue")
                && r.Prompt.Contains("analysis_default")),
            Arg.Any<CancellationToken>());
        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
            Honua.Core.Features.Authorization.Domain.OperatorResourceType.Package,
            Honua.Core.Features.Authorization.Domain.OperatorOperation.Create,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_DelegatesToGenerationServiceAndProjectsResult()
    {
        var appGen = Substitute.For<IAppGenerationService>();
        appGen.GenerateAsync(Arg.Any<AppGenerationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new AppGenerationResult
            {
                Status = "generated",
                Rationale = "Composed a field app.",
                Provider = "bedrock"
            });

        var services = new ServiceCollection()
            .AddSingleton(appGen)
            .BuildServiceProvider();

        var surface = new McpOperatorSurface(
            [new CreateAppPackageTool(_jobService, NullLogger<CreateAppPackageTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("app-1", CreateAppPackageTool.ToolName,
                """{"prompt":"a field collection app","mapPackageId":"map_123","targetSdk":"honua-sdk-js"}"""),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var structured = response.Result!.Value.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("generated");

        await appGen.Received(1).GenerateAsync(
            Arg.Is<AppGenerationRequest>(r =>
                r.Prompt.Contains("a field collection app")
                && r.Prompt.Contains("map_123")
                && r.Prompt.Contains("honua-sdk-js")),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithoutGenerationService_ReturnsCapabilityUnavailable()
    {
        var surface = new McpOperatorSurface(
            [new CreateMapPackageTool(_jobService, NullLogger<CreateMapPackageTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(new ServiceCollection().BuildServiceProvider()),
            ToolCall("map-2", CreateMapPackageTool.ToolName, """{"prompt":"a map of parcels"}"""),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("structuredContent").GetProperty("status").GetString()
            .Should().Be("capability_unavailable");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithoutPrompt_ReturnsInvalidArgument()
    {
        var mapGen = Substitute.For<IMapGenerationService>();
        var services = new ServiceCollection().AddSingleton(mapGen).BuildServiceProvider();

        var surface = new McpOperatorSurface(
            [new CreateMapPackageTool(_jobService, NullLogger<CreateMapPackageTool>.Instance)],
            [],
            NullLogger<McpOperatorSurface>.Instance);

        var response = await surface.DispatchAsync(
            AuthenticatedContext(services),
            ToolCall("map-3", CreateMapPackageTool.ToolName, """{"styleId":"style_blue"}"""),
            CancellationToken.None);

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString()
            .Should().Be("invalid_argument");

        await mapGen.DidNotReceive().GenerateAsync(Arg.Any<MapGenerationRequest>(), Arg.Any<CancellationToken>());
    }

    private static DefaultHttpContext AuthenticatedContext(IServiceProvider services)
    {
        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = services;
        return context;
    }

    private static McpJsonRpcRequest ListToolsRequest(string id) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/list"
    };

    private static McpJsonRpcRequest ToolCall(string id, string toolName, string argumentsJson) => new()
    {
        JsonRpc = "2.0",
        Id = JsonString(id),
        Method = "tools/call",
        Params = Json($$"""
            {"name":"{{toolName}}","arguments":{{argumentsJson}}}
            """)
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
