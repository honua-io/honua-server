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

    [UnitTest]
    public void PrefixMimicAndStub_DoNotGainExecutableClassification()
    {
        var unknown = McpWorkflowViewDescriptorClassifier.Describe(new Tool("honua_studio_arbitrary_code"));
        Assert.False(unknown.Meta!.Value.TryGetProperty("honua.studio", out _));
        var stub = McpWorkflowViewDescriptorClassifier.Describe(new Stub());
        Assert.False(stub.Meta!.Value.TryGetProperty("honua.studio", out _));
    }

    private class Tool(string name) : IMcpTool
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

    private sealed class Stub() : Tool("honua_query_features"), IStubMcpTool;
}
