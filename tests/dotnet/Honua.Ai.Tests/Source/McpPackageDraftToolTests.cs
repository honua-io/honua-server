// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Studio.Drafts;
using Honua.Geoprocessing;
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
/// Coverage for the deterministic package-draft MCP tools
/// (<c>honua_create_map_package</c> / <c>honua_create_app_package</c>) after
/// ADR-0076 re-founded them on the shared draft factories in <c>Honua.Core</c>.
/// </summary>
/// <remarks>
/// These run through the JSON-RPC dispatcher with the real factories composed,
/// because the assertion that matters is behavioural: the tools must return a
/// real package carrying a stable <c>map_…</c> / <c>app_…</c> identifier. A
/// roster or manifest check compares names and would pass against a tool that
/// permanently returns <c>capability_unavailable</c>, which is precisely the
/// half-finished-deletion end state ADR-0076 designs against.
/// </remarks>
[Protocol(TestProtocols.Mcp)]
public sealed class McpPackageDraftToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task ToolsList_IncludesAuthoringTools()
    {
        var response = await DispatchAsync(ListToolsRequest("list-1"));

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
    public async Task ToolsCall_CreateMapPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await DispatchAsync(ToolCall("map-1", CreateMapPackageTool.ToolName,
            """
            {
              "templateId": "analysis_default",
              "styleId": "style_choropleth",
              "themeId": "theme_operational_dark",
              "sourceBindings": [
                {"sourceId":"parcels","protocol":"ogc_features","url":"https://example.test/ogc"}
              ],
              "initialView": {"bbox":[-97.95,30.15,-97.55,30.55],"crs":4326}
            }
            """));

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("created");

        var packageId = structured.GetProperty("packageId").GetString();
        packageId.Should().StartWith("map_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://map-packages/" + packageId);

        // A real package, not an empty envelope.
        var package = structured.GetProperty("package");
        package.GetProperty("mapPackageId").GetString().Should().Be(packageId);
        package.GetProperty("format").GetString().Should().Be("honua_map_package.v1");
        package.GetProperty("status").GetString().Should().Be("Draft");
        package.GetProperty("templateId").GetString().Should().Be("analysis_default");

        await _jobService.Received(1).EnsureCallerAuthorizedAsync(
            Arg.Any<System.Security.Claims.ClaimsPrincipal>(),
            Honua.Core.Features.Authorization.Domain.OperatorResourceType.Package,
            Honua.Core.Features.Authorization.Domain.OperatorOperation.Create,
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_HonoursSourceBindingsAndInitialView()
    {
        // Both inputs were defects before D5: sourceBindings was not accepted at
        // all, and initialView was published in the schema then dropped by the
        // parser. An integer crs is the standard's own alternate spelling.
        var response = await DispatchAsync(ToolCall("map-2", CreateMapPackageTool.ToolName,
            """
            {
              "sourceBindings": [
                {"sourceId":"parcels","protocol":"ogc_features","url":"https://example.test/ogc","layerId":"7"}
              ],
              "initialView": {"bbox":[-97.95,30.15,-97.55,30.55],"crs":4326}
            }
            """));

        var package = response!.Result!.Value.GetProperty("structuredContent").GetProperty("package");

        var binding = package.GetProperty("sourceBindings").EnumerateArray().Single();
        binding.GetProperty("sourceId").GetString().Should().Be("parcels");
        binding.GetProperty("protocol").GetString().Should().Be("ogc_features");
        binding.GetProperty("locator").GetProperty("url").GetString().Should().Be("https://example.test/ogc");
        binding.GetProperty("locator").GetProperty("layerId").GetString().Should().Be("7");

        var view = package.GetProperty("initialView");
        view.GetProperty("crs").GetString().Should().Be("EPSG:4326");
        view.GetProperty("bbox").EnumerateArray().Select(v => v.GetDouble())
            .Should().Equal(-97.95, 30.15, -97.55, 30.55);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithoutPrompt_StillCreatesAPackage()
    {
        // The pre-D5 implementation threw invalid_argument here: a fully
        // structured call with no prose was a hard error.
        var response = await DispatchAsync(ToolCall("map-3", CreateMapPackageTool.ToolName,
            """{"styleId":"style_blue"}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("structuredContent").GetProperty("packageId").GetString().Should().StartWith("map_");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithPrompt_IgnoresIt()
    {
        // A caller that still sends prose gets the same draft as one that does
        // not: the prompt is not a modelled member, so it cannot reach the
        // factory and cannot change the output.
        var withPrompt = await DispatchAsync(ToolCall("map-4", CreateMapPackageTool.ToolName,
            """{"prompt":"a beautiful map of parcels in red","styleId":"style_blue"}"""));
        var withoutPrompt = await DispatchAsync(ToolCall("map-5", CreateMapPackageTool.ToolName,
            """{"styleId":"style_blue"}"""));

        static JsonElement PackageOf(McpJsonRpcResponse? response) =>
            response!.Result!.Value.GetProperty("structuredContent").GetProperty("package");

        // Everything except the freshly minted identity must match.
        var promptedStyle = PackageOf(withPrompt).GetProperty("styleRefs").EnumerateArray().Single();
        var plainStyle = PackageOf(withoutPrompt).GetProperty("styleRefs").EnumerateArray().Single();
        promptedStyle.GetProperty("styleId").GetString().Should().Be("style_blue");
        plainStyle.GetProperty("styleId").GetString().Should().Be("style_blue");
        PackageOf(withPrompt).TryGetProperty("rationale", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithUnorderedBbox_ReturnsInvalidArgument()
    {
        var response = await DispatchAsync(ToolCall("map-6", CreateMapPackageTool.ToolName,
            """{"initialView":{"bbox":[10,-1,-10,1]}}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().Contain("bboxNotOrdered");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await DispatchAsync(ToolCall("app-1", CreateAppPackageTool.ToolName,
            """
            {
              "templateId": "analysis_dashboard",
              "targetSdk": "honua-sdk-js",
              "mapPackageId": "map_5a90",
              "boundArtifactIds": ["artifact_summary_report"],
              "runtimeConfig": {"title":"Flood Exposure Dashboard"}
            }
            """));

        response.Should().NotBeNull();
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();

        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("status").GetString().Should().Be("created");

        var packageId = structured.GetProperty("packageId").GetString();
        packageId.Should().StartWith("app_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://app-packages/" + packageId);

        var package = structured.GetProperty("package");
        package.GetProperty("appPackageId").GetString().Should().Be(packageId);
        package.GetProperty("format").GetString().Should().Be("honua_app_package.v1");
        package.GetProperty("status").GetString().Should().Be("Draft");
        package.GetProperty("mapPackageId").GetString().Should().Be("map_5a90");
        package.GetProperty("runtimeConfig").GetProperty("title").GetString().Should().Be("Flood Exposure Dashboard");

        // Retained knowledge §5: sharing stays closed by default.
        var sharePolicy = package.GetProperty("sharePolicy");
        sharePolicy.GetProperty("visibility").GetString().Should().Be("private");
        sharePolicy.GetProperty("embed").GetBoolean().Should().BeFalse();
        sharePolicy.GetProperty("reviewed").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_WithoutPrompt_StillCreatesAPackage()
    {
        var response = await DispatchAsync(ToolCall("app-2", CreateAppPackageTool.ToolName, "{}"));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("packageId").GetString().Should().StartWith("app_");

        // Absent targetSdk falls back to the standard's declared default, and the
        // unbound map package is a deferred warning rather than a failure.
        structured.GetProperty("package").GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        structured.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("code").GetString())
            .Should().Contain("bindingNotResolved");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app_package")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_WithDuplicateArtifacts_ReturnsInvalidArgument()
    {
        var response = await DispatchAsync(ToolCall("app-3", CreateAppPackageTool.ToolName,
            """{"boundArtifactIds":["artifact_a","artifact_a"]}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    private Task<McpJsonRpcResponse?> DispatchAsync(McpJsonRpcRequest request)
    {
        var surface = new McpDataAccessSurface(
            [
                new CreateMapPackageTool(
                    _jobService,
                    new MapPackageDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                    new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                    NullLogger<CreateMapPackageTool>.Instance),
                new CreateAppPackageTool(
                    _jobService,
                    new AppPackageDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                    new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                    NullLogger<CreateAppPackageTool>.Instance),
            ],
            [],
            NullLogger<McpDataAccessSurface>.Instance);

        var context = McpTestFactory.AuthenticatedHttpContext();
        context.RequestServices = new ServiceCollection().BuildServiceProvider();

        return surface.DispatchAsync(context, request, CancellationToken.None);
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
