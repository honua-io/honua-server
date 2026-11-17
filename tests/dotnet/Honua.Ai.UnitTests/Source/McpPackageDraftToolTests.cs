// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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

[Protocol(TestProtocols.Mcp)]
public sealed class McpPackageDraftToolTests
{
    private readonly IGeoprocessingJobService _jobService = Substitute.For<IGeoprocessingJobService>();

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/list")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/list")]
    public async Task MapAppVocabulary_ToolsListUsesCanonicalNamesWithoutAliases()
    {
        var response = await DispatchAsync(ListToolsRequest("vocabulary-list"));
        var names = response!.Result!.Value.GetProperty("tools").EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()).ToArray();
        names.Should().Contain(["honua_create_map", "honua_create_app"]);
        names.Should().NotContain(["honua_create_map_package", "honua_create_app_package"]);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("app")]
    [Trait("Category", "Unit")]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task MapAppVocabulary_CreateReturnsCanonicalIdentityAndResource(string kind)
    {
        var response = await DispatchAsync(ToolCall("vocabulary-create", "honua_create_" + kind, "{}"));
        response!.Error.Should().BeNull();
        var result = response.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var content = result.GetProperty("structuredContent");
        var id = content.GetProperty(kind + "Id").GetString();
        id.Should().StartWith(kind + "_");
        content.GetProperty("resourceUri").GetString().Should().Be("honua://" + kind + "s/" + id);
        content.GetProperty(kind).GetProperty(kind + "Id").GetString().Should().Be(id);
        content.TryGetProperty("packageId", out _).Should().BeFalse();
        content.TryGetProperty("package", out _).Should().BeFalse();
    }

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

        names.Should().Contain([CreateMapTool.ToolName, CreateAppTool.ToolName]);
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await DispatchAsync(ToolCall("map-1", CreateMapTool.ToolName,
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

        var packageId = structured.GetProperty("mapId").GetString();
        packageId.Should().StartWith("map_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://maps/" + packageId);

        // A real package, not an empty envelope.
        var package = structured.GetProperty("map");
        package.GetProperty("mapId").GetString().Should().Be(packageId);
        package.GetProperty("format").GetString().Should().Be("honua_map.v1");
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
    [Endpoint("POST /mcp tools/call honua_create_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_HonoursSourceBindingsAndInitialView()
    {
        // Both inputs were defects before D5: sourceBindings was not accepted at
        // all, and initialView was published in the schema then dropped by the
        // parser. An integer crs is the standard's own alternate spelling.
        var response = await DispatchAsync(ToolCall("map-2", CreateMapTool.ToolName,
            """
            {
              "sourceBindings": [
                {"sourceId":"parcels","protocol":"ogc_features","url":"https://example.test/ogc","layerId":"7"}
              ],
              "initialView": {"bbox":[-97.95,30.15,-97.55,30.55],"crs":4326}
            }
            """));

        var package = response!.Result!.Value.GetProperty("structuredContent").GetProperty("map");

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
    [Endpoint("POST /mcp tools/call honua_create_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithoutPrompt_StillCreatesAPackage()
    {
        // The pre-D5 implementation threw invalid_argument here: a fully
        // structured call with no prose was a hard error.
        var response = await DispatchAsync(ToolCall("map-3", CreateMapTool.ToolName,
            """{"styleId":"style_blue"}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        result.GetProperty("structuredContent").GetProperty("mapId").GetString().Should().StartWith("map_");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithPrompt_IgnoresIt()
    {
        // A caller that still sends prose gets the same draft as one that does
        // not: the prompt is not a modelled member, so it cannot reach the
        // factory and cannot change the output.
        var withPrompt = await DispatchAsync(ToolCall("map-4", CreateMapTool.ToolName,
            """{"prompt":"a beautiful map of parcels in red","styleId":"style_blue"}"""));
        var withoutPrompt = await DispatchAsync(ToolCall("map-5", CreateMapTool.ToolName,
            """{"styleId":"style_blue"}"""));

        static JsonElement PackageOf(McpJsonRpcResponse? response) =>
            response!.Result!.Value.GetProperty("structuredContent").GetProperty("map");

        // Everything except the freshly minted identity must match.
        var promptedStyle = PackageOf(withPrompt).GetProperty("styleRefs").EnumerateArray().Single();
        var plainStyle = PackageOf(withoutPrompt).GetProperty("styleRefs").EnumerateArray().Single();
        promptedStyle.GetProperty("styleId").GetString().Should().Be("style_blue");
        plainStyle.GetProperty("styleId").GetString().Should().Be("style_blue");
        PackageOf(withPrompt).TryGetProperty("rationale", out _).Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateMapPackage_WithUnorderedBbox_ReturnsInvalidArgument()
    {
        var response = await DispatchAsync(ToolCall("map-6", CreateMapTool.ToolName,
            """{"initialView":{"bbox":[10,-1,-10,1]}}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("invalid_argument");
        structured.GetProperty("message").GetString().Should().Contain("bboxNotOrdered");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_ReturnsRealPackageWithStableIdentifier()
    {
        var response = await DispatchAsync(ToolCall("app-1", CreateAppTool.ToolName,
            """
            {
              "templateId": "analysis_dashboard",
              "targetSdk": "honua-sdk-js",
              "mapId": "map_5a90",
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

        var packageId = structured.GetProperty("appId").GetString();
        packageId.Should().StartWith("app_");
        structured.GetProperty("resourceUri").GetString().Should().Be("honua://apps/" + packageId);

        var package = structured.GetProperty("app");
        package.GetProperty("appId").GetString().Should().Be(packageId);
        package.GetProperty("format").GetString().Should().Be("honua_app.v1");
        package.GetProperty("status").GetString().Should().Be("Draft");
        package.GetProperty("mapId").GetString().Should().Be("map_5a90");
        package.GetProperty("runtimeConfig").GetProperty("title").GetString().Should().Be("Flood Exposure Dashboard");

        // Retained knowledge §5: sharing stays closed by default.
        var sharePolicy = package.GetProperty("sharePolicy");
        sharePolicy.GetProperty("visibility").GetString().Should().Be("private");
        sharePolicy.GetProperty("embed").GetBoolean().Should().BeFalse();
        sharePolicy.GetProperty("reviewed").GetBoolean().Should().BeFalse();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_WithoutPrompt_StillCreatesAPackage()
    {
        var response = await DispatchAsync(ToolCall("app-2", CreateAppTool.ToolName, "{}"));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeFalse();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("appId").GetString().Should().StartWith("app_");

        // Absent targetSdk falls back to the standard's declared default, and the
        // unbound map package is a deferred warning rather than a failure.
        structured.GetProperty("app").GetProperty("targetSdk").GetString().Should().Be("honua-sdk-js");
        structured.GetProperty("warnings").EnumerateArray()
            .Select(w => w.GetProperty("code").GetString())
            .Should().Contain("bindingNotResolved");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_app")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task ToolsCall_CreateAppPackage_WithDuplicateArtifacts_ReturnsInvalidArgument()
    {
        var response = await DispatchAsync(ToolCall("app-3", CreateAppTool.ToolName,
            """{"boundArtifactIds":["artifact_a","artifact_a"]}"""));

        var result = response!.Result!.Value;
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.GetProperty("structuredContent").GetProperty("code").GetString().Should().Be("invalid_argument");
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map")]
    public async Task Create_RecordsTheCaller_AndHidesTheDraftFromAnotherOwner()
    {
        var drafts = new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System);
        var mapTool = new CreateMapTool(
            _jobService,
            new MapDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
            drafts,
            NullLogger<CreateMapTool>.Instance);
        var appTool = new CreateAppTool(
            _jobService,
            new AppDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
            drafts,
            NullLogger<CreateAppTool>.Instance);
        using var arguments = JsonDocument.Parse("{}");
        var caller = McpTestFactory.AuthenticatedHttpContext("ada");

        var mapResult = await mapTool.InvokeAsync(caller, arguments.RootElement.Clone(), CancellationToken.None);
        var appResult = await appTool.InvokeAsync(caller, arguments.RootElement.Clone(), CancellationToken.None);
        var mapId = mapResult.StructuredContent!.Value.GetProperty("mapId").GetString()!;
        var appId = appResult.StructuredContent!.Value.GetProperty("appId").GetString()!;

        (await drafts.GetMapDraftAsync(mapId, "ada")).Should().NotBeNull();
        (await drafts.GetAppDraftAsync(appId, "ada")).Should().NotBeNull();
        (await drafts.GetMapDraftAsync(mapId, "mallory")).Should().BeNull();
        (await drafts.GetAppDraftAsync(appId, "mallory")).Should().BeNull();
    }

    [UnitTest]
    [Operation(Operations.Create)]
    [Endpoint("POST /mcp tools/call honua_create_map")]
    public async Task Create_WithoutOwnerSubject_DoesNotSave()
    {
        var drafts = new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System);
        var tool = new CreateMapTool(
            _jobService,
            new MapDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
            drafts,
            NullLogger<CreateMapTool>.Instance);
        using var arguments = JsonDocument.Parse("{}");
        var context = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(authenticationType: "Test")),
        };

        var act = async () => await tool.InvokeAsync(context, arguments.RootElement, CancellationToken.None);

        await act.Should().ThrowAsync<GeoprocessingAuthorizationException>()
            .WithMessage("*owner*");
    }

    private Task<McpJsonRpcResponse?> DispatchAsync(McpJsonRpcRequest request)
    {
        var surface = new McpDataAccessSurface(
            [
                new CreateMapTool(
                    _jobService,
                    new MapDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                    new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                    NullLogger<CreateMapTool>.Instance),
                new CreateAppTool(
                    _jobService,
                    new AppDraftFactory(new GuidDraftIdentifierGenerator(), TimeProvider.System),
                    new InMemoryPackageDraftStore(new PackageDraftRetentionOptions(), TimeProvider.System),
                    NullLogger<CreateAppTool>.Instance),
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
        Method = "tools/list",
        Params = Json("""{"view":"full"}""")
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
