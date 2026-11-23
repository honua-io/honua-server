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

// The operating skills address the metadata, access and export tools by view: each view must
// publish exactly its tools, canonically, within budget.
public sealed partial class McpWorkflowViewTests
{
    private static readonly string[] MetadataViewToolNames =
    [
        "honua_list_layers",
        "honua_describe_layer",
        "honua_admin_services_list",
        "honua_admin_services_settings_get",
        "honua_admin_layer_fields_get",
        "honua_admin_layer_popup_info_get",
        "honua_admin_services_layer_metadata_set",
        "honua_admin_metadata_service_discovery_set",
        "honua_admin_metadata_layer_discovery_set",
        "honua_admin_services_timeinfo_set",
        "honua_admin_layer_fields_set",
        "honua_admin_layer_popup_info_set",
    ];

    private static readonly string[] AccessViewToolNames =
    [
        "honua_admin_api_key_list",
        "honua_admin_api_key_effective_permissions",
        "honua_admin_services_list",
        "honua_admin_services_settings_get",
        "honua_admin_role_list",
        "honua_admin_role_get",
        "honua_admin_role_permissions_get",
        "honua_admin_user_list",
        "honua_admin_user_get",
        "honua_admin_user_effective_permissions",
        "honua_admin_services_access_policy_set",
        "honua_admin_role_permissions_set",
        "honua_admin_user_roles_update",
        "honua_admin_api_key_update_restrictions",
        "honua_admin_api_key_revoke",
    ];

    private static readonly string[] ExportToolNames =
    [
        "honua_admin_exports_list",
        "honua_admin_exports_get",
        "honua_admin_exports_create",
        "honua_admin_exports_delete",
    ];

    private static readonly string[] ExportsViewToolNames = ["honua_list_layers", .. ExportToolNames];

    [UnitTest]
    public async Task ToolsList_MetadataAccessAndExportsViews_AreBoundedCanonicalAndRequireAuthentication()
    {
        await using var provider = BuildLiveProvider();
        var surface = BuildFullSurface(provider.GetServices<IMcpToolSource>().ToArray());
        var full = await surface.GetAllToolsAsync();

        foreach (var (viewName, expected) in new[]
                 {
                     (McpWorkflowViewCatalog.MetadataViewName, MetadataViewToolNames),
                     (McpWorkflowViewCatalog.AccessViewName, AccessViewToolNames),
                     (McpWorkflowViewCatalog.ExportsViewName, ExportsViewToolNames),
                 })
        {
            var view = await ListToolsAsync(surface, viewName);
            view.Names.Should().BeEquivalentTo(expected, viewName);
            view.NextCursor.Should().BeNull();
            view.Meta.GetProperty("descriptorBytes").GetInt32().Should().BeLessThanOrEqualTo(128 * 1024);
            view.Tools.Should().OnlyContain(tool => Encoding.UTF8.GetByteCount(tool.GetRawText()) <= 16 * 1024);
            foreach (var tool in view.Tools)
            {
                var canonical = full.Single(candidate => candidate.Name == tool.GetProperty("name").GetString());
                JsonSerializer.Serialize(McpWorkflowViewDescriptorClassifier.Describe(canonical), McpJsonContext.Default.McpToolDescriptor)
                    .Should().Be(tool.GetRawText());
            }

            (await ListToolsAsync(surface, viewName, McpTestFactory.AnonymousHttpContext())).Names
                .Should().BeEmpty($"anonymous callers see no {viewName} tools");
        }
    }

    [UnitTest]
    public async Task ExportTools_AreInTheExportsView_WithReadOnlyAndDestructiveAnnotations()
    {
        await using var provider = BuildLiveProvider();
        var surface = BuildFullSurface(provider.GetServices<IMcpToolSource>().ToArray());

        var exports = await ListToolsAsync(surface, McpWorkflowViewCatalog.ExportsViewName);
        StageOf(exports, "export").Should().BeEquivalentTo(ExportsViewToolNames);
        (await ListToolsAsync(surface, McpWorkflowViewCatalog.ConfigureViewName)).Names
            .Should().NotIntersectWith(ExportToolNames, "configure stays inside its descriptor budget");

        var tools = exports.Tools.ToDictionary(tool => tool.GetProperty("name").GetString()!, StringComparer.Ordinal);
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
        (await ListToolsAsync(surface, McpWorkflowViewCatalog.ExportsViewName, anonymous)).Names
            .Should().BeEmpty("anonymous callers see no export tools");
        var refused = await DispatchAsync(surface,
            """{"jsonrpc":"2.0","id":"export","method":"tools/call","params":{"name":"honua_admin_exports_list","arguments":{}}}""",
            anonymous);
        refused!.Result!.Value.GetProperty("isError").GetBoolean().Should().BeTrue();
    }
}
