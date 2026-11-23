// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

public sealed partial class McpWorkflowViewTests
{
    private static readonly string[] OperateJobToolNames =
    [
        "honua_list_jobs",
        "honua_diagnose_job",
        "honua_job_evidence",
        "honua_cancel_job",
    ];

    [UnitTest]
    public async Task ToolsList_OperateView_InvestigatesJobsWithoutSwitchingViews()
    {
        var surface = BuildFullSurface();
        var full = await surface.GetAllToolsAsync();

        var view = await ListToolsAsync(surface, McpWorkflowViewCatalog.OperateViewName);

        view.Meta.GetProperty("revision").GetString().Should().Be("operate.v3");
        StageOf(view, "investigate").Should().BeEquivalentTo(OperateJobToolNames);
        view.Names.Length.Should().BeLessThanOrEqualTo(48);
        view.NextCursor.Should().BeNull();
        foreach (var name in OperateJobToolNames)
        {
            var descriptor = view.Tools.Single(tool => tool.GetProperty("name").GetString() == name);
            JsonSerializer.Serialize(full.Single(tool => tool.Name == name).Describe(), McpJsonContext.Default.McpToolDescriptor)
                .Should().Be(descriptor.GetRawText(), "a view projects the canonical descriptor");
        }

        RequiredInputs(view, "honua_diagnose_job").Should().Contain("jobId");
        RequiredInputs(view, "honua_job_evidence").Should().Contain("jobId");
        RequiredInputs(view, "honua_cancel_job").Should().Contain("jobId");
        Annotation(view, "honua_cancel_job", "readOnlyHint").Should().BeFalse("cancel changes job state");
        Annotation(view, "honua_diagnose_job", "readOnlyHint").Should().BeTrue();
    }

    private static string?[] RequiredInputs(ToolsListView view, string toolName) =>
        view.Tools.Single(tool => tool.GetProperty("name").GetString() == toolName)
            .GetProperty("inputSchema").GetProperty("required").EnumerateArray()
            .Select(item => item.GetString()).ToArray();

    private static bool Annotation(ToolsListView view, string toolName, string hint) =>
        view.Tools.Single(tool => tool.GetProperty("name").GetString() == toolName)
            .GetProperty("annotations").GetProperty(hint).GetBoolean();
}
