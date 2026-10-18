// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Capabilities;
using Honua.Server.Features.AgentWorkspace;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceCapabilityRegistryTests
{
    [UnitTest]
    public void EveryActualWorkspaceTool_HasOneMatchingDynamicRegistryDescriptor()
    {
        var registry = new CapabilityRegistry();
        foreach (var tool in AgentWorkspaceTool.All)
        {
            var descriptor = registry.All.Should().ContainSingle(value => value.McpToolName == tool.Name).Subject;
            descriptor.Category.Should().Be(tool.WorkflowFamily);
            descriptor.IsDynamic.Should().BeTrue();
            descriptor.Maturity.Should().Be(CapabilityMaturity.Implemented);
        }
    }
}
