// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

// The operating skills address the export tools by view (skill refresh plan section 3, #9445).
public sealed partial class McpWorkflowViewTests
{
    private static readonly string[] ExportToolNames =
    [
        "honua_admin_exports_list",
        "honua_admin_exports_get",
        "honua_admin_exports_create",
        "honua_admin_exports_delete",
    ];

    [UnitTest]
    public async Task ExportTools_AreInTheConfigureExportStage_WithReadOnlyAndDestructiveAnnotations()
    {
        await using var provider = BuildLiveProvider();
        var surface = BuildFullSurface(provider.GetServices<IMcpToolSource>().ToArray());

        var configure = await ListToolsAsync(surface, McpWorkflowViewCatalog.ConfigureViewName);
        StageOf(configure, "export").Should().BeEquivalentTo(ExportToolNames);
        configure.NextCursor.Should().BeNull();
        configure.Meta.GetProperty("descriptorBytes").GetInt32().Should().BeLessThanOrEqualTo(128 * 1024);

        var tools = configure.Tools.ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
        foreach (var readTool in new[] { "honua_admin_exports_list", "honua_admin_exports_get" })
        {
            tools[readTool].GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue(readTool);
        }

        foreach (var changeTool in new[] { "honua_admin_exports_create", "honua_admin_exports_delete" })
        {
            var annotations = tools[changeTool].GetProperty("annotations");
            annotations.GetProperty("readOnlyHint").GetBoolean().Should().BeFalse(changeTool);
            annotations.GetProperty("destructiveHint").GetBoolean().Should().BeTrue(changeTool);
        }

        // Every required export input names where its value comes from.
        var getSchema = tools["honua_admin_exports_get"].GetProperty("inputSchema");
        getSchema.GetProperty("required").EnumerateArray().Select(field => field.GetString()).Should().Contain("exportId");
        getSchema.GetProperty("properties").GetProperty("exportId").GetProperty("description").GetString()
            .Should().Contain("honua_admin_exports_list");
        tools["honua_admin_exports_create"].GetProperty("inputSchema").GetProperty("properties").GetProperty("source")
            .GetProperty("description").GetString().Should().Contain("honua_list_layers");

        var anonymous = McpTestFactory.AnonymousHttpContext();
        (await ListToolsAsync(surface, McpWorkflowViewCatalog.ConfigureViewName, anonymous)).Names
            .Should().NotIntersectWith(ExportToolNames);
        var refused = await DispatchAsync(surface,
            """{"jsonrpc":"2.0","id":"export","method":"tools/call","params":{"name":"honua_admin_exports_list","arguments":{}}}""",
            anonymous);
        refused!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
    }
}
