// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceCapabilityRegistryTests
{
    [UnitTest]
    public void EveryActualWorkspaceTool_HasOneMatchingDynamicRegistryDescriptor()
    {
        var registry = new CapabilityRegistry();
        var tools = AgentWorkspaceTool.All
            .Concat(AgentWorkspaceTool.DatasetImportTools)
            .Concat(AgentWorkspaceTool.DatasetImportAuthorityReadTools)
            .Concat(AgentWorkspaceTool.DatasetImportAuthorityRenewTools)
            .Concat(AgentWorkspaceTool.DatasetImportSubmissionTools)
            .Concat(LocalProcessWorkspaceTool.All).ToArray();
        tools.Select(tool => tool.Name).Should().OnlyHaveUniqueItems();
        registry.All.Where(descriptor => descriptor.McpToolName?.StartsWith("honua_workspace_", StringComparison.Ordinal) == true)
            .Select(descriptor => descriptor.McpToolName).Should().BeEquivalentTo(tools.Select(tool => tool.Name));
        foreach (var tool in tools)
        {
            var descriptor = registry.All.Should().ContainSingle(value => value.McpToolName == tool.Name).Subject;
            descriptor.Category.Should().Be(tool.WorkflowFamily);
            descriptor.IsDynamic.Should().BeTrue();
            descriptor.Maturity.Should().Be(CapabilityMaturity.Implemented);
        }
    }
}
