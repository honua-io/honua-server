// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceContractTests
{
    [UnitTest]
    public void MissingRevisionCannotImplicitlyAuthorizeEditingRevisionZero()
    {
        var missingRevision = () => JsonSerializer.Deserialize("""{"viewId":"tab-1","changes":{"layerName":"roads"}}""",
            AgentWorkspaceJsonContext.Default.PatchAgentDraft);
        missingRevision.Should().Throw<JsonException>();
        var explicitRevision = JsonSerializer.Deserialize("""{"expectedRevision":0,"viewId":"tab-1","changes":{"layerName":"roads"}}""",
            AgentWorkspaceJsonContext.Default.PatchAgentDraft);
        explicitRevision!.ExpectedRevision.Should().Be(0);
    }

    [UnitTest]
    public void WorkspaceToolsHaveBoundedTypedContractsAndExplicitServerFamily()
    {
        var descriptors = AgentWorkspaceTool.All.Select(tool => tool.Describe()).ToArray();
        descriptors.Select(tool => tool.Name).Should().OnlyHaveUniqueItems();
        foreach (var descriptor in descriptors)
        {
            descriptor.InputSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();
            descriptor.Meta!.Value.GetProperty("honua.studio").GetProperty("family").GetString().Should().Be("honua.workspace.canonical");
            foreach (var required in descriptor.InputSchema.GetProperty("required").EnumerateArray())
            {
                descriptor.InputSchema.GetProperty("properties").TryGetProperty(required.GetString()!, out _).Should().BeTrue();
            }
        }
        descriptors.Single(tool => tool.Name == "honua_workspace_submit_draft").Annotations!.ReadOnlyHint.Should().BeFalse();
    }

    [UnitTest]
    public async Task MissingRedisNeverFallsBackToVolatileWorkspaceSuccess()
    {
        var store = new AgentWorkspaceStore(null, "test-deployment");
        store.IsAvailable.Should().BeFalse();
        var action = () => store.GetTargetIdAsync();
        await action.Should().ThrowAsync<AgentWorkspaceUnavailableException>();
        (await new AgentWorkspaceToolSource(store).GetToolsAsync(CancellationToken.None)).Should().BeEmpty();
    }
}
