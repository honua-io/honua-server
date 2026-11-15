// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpWorkflowViewTests
{
    private static readonly string[] InspectionToolNames =
    [
        "honua_admin_import_formats",
        "honua_admin_import_limits",
        "honua_admin_import_preview",
        "honua_admin_import_preview_url",
        "honua_admin_connections_tables_validate",
    ];

    [UnitTest]
    public async Task ToolsList_ConfigureInspection_UsesOnlyAuditedReadOnlyProjection()
    {
        await using var provider = BuildLiveProvider();
        var sources = provider.GetServices<IMcpToolSource>().ToArray();
        var projected = (await Task.WhenAll(sources.Select(async source =>
            await source.GetToolsAsync(CancellationToken.None)))).SelectMany(tools => tools).ToArray();
        projected.Select(tool => tool.Name).Should().Contain(InspectionToolNames);
        projected.Select(tool => tool.Name).Should().NotContain(
        [
            "honua_admin_import_upload",
            "honua_admin_connections_update",
            "honua_admin_connections_test_draft",
        ], "inspection discovery must not opt additional mutations or credentials into the audited roster");
    }

    [UnitTest]
    public async Task ToolsList_ConfigureInspection_UsesCanonicalDescriptorsWithinBudget()
    {
        await using var provider = BuildLiveProvider();
        var sources = provider.GetServices<IMcpToolSource>().ToArray();
        var surface = BuildFullSurface(sources);
        var full = await surface.GetAllToolsAsync();
        var view = await ListToolsAsync(surface, "configure");
        StageOf(view, "inspect").Should().BeEquivalentTo(InspectionToolNames);
        view.NextCursor.Should().BeNull();
        view.Names.Length.Should().BeLessThanOrEqualTo(48);
        view.Meta.GetProperty("descriptorBytes").GetInt32().Should().BeLessThanOrEqualTo(128 * 1024);
        view.Tools.Should().OnlyContain(tool => Encoding.UTF8.GetByteCount(tool.GetRawText()) <= 16 * 1024);
        AssertCanonicalDescriptors(view, full, InspectionToolNames);
        AssertRequiredInputs(view, "honua_admin_import_preview", "file", "fileName");
        AssertRequiredInputs(view, "honua_admin_import_preview_url", "sourceUrl");
        AssertRequiredInputs(view, "honua_admin_connections_tables_validate", "id", "schema", "table");

        _output.WriteLine($"configure inspection: {view.Names.Length} descriptors, "
            + $"{view.Meta.GetProperty("descriptorBytes").GetInt32()} bytes");
    }

    private static void AssertCanonicalDescriptors(ToolsListView view, IReadOnlyList<IMcpTool> full, string[] expected)
    {
        foreach (var name in expected)
        {
            var descriptor = view.Tools.Single(tool => tool.GetProperty("name").GetString() == name);
            var canonical = full.Single(tool => tool.Name == name).Describe();
            JsonSerializer.Serialize(canonical, McpJsonContext.Default.McpToolDescriptor)
                .Should().Be(descriptor.GetRawText());
            descriptor.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
            descriptor.GetProperty("inputSchema").GetProperty("type").GetString().Should().Be("object");
        }
    }

    private static void AssertRequiredInputs(ToolsListView view, string toolName, params string[] expected)
    {
        var schema = view.Tools.Single(tool => tool.GetProperty("name").GetString() == toolName)
            .GetProperty("inputSchema");
        schema.GetProperty("required").EnumerateArray().Select(item => item.GetString())
            .Should().BeEquivalentTo(expected);
        foreach (var name in expected)
        {
            schema.GetProperty("properties").GetProperty(name).GetProperty("description").GetString()
                .Should().MatchRegex("Caller-supplied|Source: honua_admin_");
        }
    }
}
