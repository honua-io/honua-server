// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.Protocols.Mcp.Views;
using Honua.Geoprocessing;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.StudioAiProxy;

/// <summary>Studio clients can discover real catalog sources without routing unrelated tools.</summary>
[Protocol(TestProtocols.TestQuality)]
public sealed class StudioAssistantCatalogTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Describe_CatalogTool_RoutesCanonicalReadOnlyDescriptor(bool describe)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        IMcpTool tool = describe
            ? new DescribeLayerTool(jobs, NullLogger<DescribeLayerTool>.Instance)
            : new ListLayersTool(jobs, NullLogger<ListLayersTool>.Instance);
        var original = tool.Describe();
        var descriptor = McpWorkflowViewDescriptorClassifier.Describe(tool);
        descriptor.Meta.Should().NotBeNull();
        var metadata = descriptor.Meta!.Value.GetProperty("honua.studio");
        metadata.GetProperty("family").GetString().Should().Be("honua.studio.composition");
        metadata.GetProperty("view").GetString().Should().Be("setup");
        descriptor.InputSchema.GetRawText().Should().Be(original.InputSchema.GetRawText());
        descriptor.Annotations!.ReadOnlyHint.Should().BeTrue();
    }

    [UnitTest]
    public void Describe_UnrelatedTool_DoesNotInferStudioAuthorityFromItsName()
    {
        var tool = Substitute.For<IMcpTool>();
        tool.Describe().Returns(new McpToolDescriptor
        {
            Name = "honua_list_layers",
            Description = "Fixture",
            InputSchema = JsonSerializer.SerializeToElement(new { type = "object" })
        });
        McpWorkflowViewDescriptorClassifier.Describe(tool).Meta.Should().BeNull();
    }
}
