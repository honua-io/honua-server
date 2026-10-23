// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Only server-authored canonical workflow membership grants native tool classification.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class WorkspaceToolClassificationTests
{
    [UnitTest]
    public void CanonicalTool_GetsSeparateWorkspaceFamilyAndPreservesExistingMetadata()
    {
        var descriptor = McpWorkflowViewDescriptorClassifier.Describe(new Tool("honua_query_features"));
        var metadata = descriptor.Meta!.Value;
        Assert.Equal("honua.workspace.canonical", metadata.GetProperty("honua.studio").GetProperty("family").GetString());
        Assert.Equal("preserved", metadata.GetProperty("other").GetString());
    }

    [UnitTheory]
    [InlineData("honua_studio_arbitrary_code")]
    [InlineData("honua_unknown_arbitrary_code")]
    public void ToolOutsideServerAuthoredViews_DoesNotGainClassification(string name)
    {
        var unknown = McpWorkflowViewDescriptorClassifier.Describe(new Tool(name));
        Assert.False(unknown.Meta!.Value.TryGetProperty("honua.studio", out _));
    }

    [UnitTheory]
    [InlineData("honua_workspace_get_turn", "honua.workspace.canonical", "workspace")]
    [InlineData("honua_workspace_local_process_run", "honua.workspace.local-process", "processes")]
    public void WorkspaceFamily_UsesServerAuthoredPrefixRules(string name, string family, string view)
    {
        var descriptor = McpWorkflowViewDescriptorClassifier.Describe(new Tool(name));
        var classification = descriptor.Meta!.Value.GetProperty("honua.studio");
        Assert.Equal(family, classification.GetProperty("family").GetString());
        Assert.Equal(view, classification.GetProperty("view").GetString());
        Assert.Equal("preserved", descriptor.Meta.Value.GetProperty("other").GetString());
    }

    private sealed class Tool(string name) : IMcpTool
    {
        public string Name => name;
        public string WorkflowFamily => "lifecycle";
        public McpToolDescriptor Describe() => new()
        {
            Name = Name,
            Description = "Canonical descriptor",
            InputSchema = JsonDocument.Parse("{\"type\":\"object\"}").RootElement.Clone(),
            Meta = JsonDocument.Parse("{\"other\":\"preserved\"}").RootElement.Clone()
        };
        public Task<McpToolsCallResult> InvokeAsync(HttpContext httpContext, JsonElement? arguments, CancellationToken cancellationToken)
            => Task.FromResult(new McpToolsCallResult());
    }
}
