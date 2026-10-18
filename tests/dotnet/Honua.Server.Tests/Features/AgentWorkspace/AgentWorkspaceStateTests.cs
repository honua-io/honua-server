// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.AgentWorkspace;

public sealed class AgentWorkspaceStateTests
{
    private static AgentWorkspaceDocument Create()
    {
        var document = new AgentWorkspaceDocument
        {
            WorkspaceId = "workspace-1",
            TargetId = "target-1",
            TenantId = "tenant-1",
            OwnerId = "human-1",
            Members = ["human-1", "agent-1"],
        };
        document.Views["tab-1"] = [AgentWorkspaceState.Current(document, "tab-1")];
        document.Views["tab-2"] = [AgentWorkspaceState.Current(document, "tab-2")];
        return document;
    }

    [UnitTest]
    public void PromptUsesSubmittedRevisionDespiteLaterNavigationAndSeparateTab()
    {
        var document = AgentWorkspaceState.Update(Create(), new("tab-1", 0, "target-1", [new("layer", "1")], null, "console"));
        document = AgentWorkspaceState.Update(document, new("tab-1", 1, "target-1", [new("layer", "2")], null, "console"));
        document = AgentWorkspaceState.Update(document, new("tab-2", 0, "target-1", [new("layer", "3")], null, "console"));
        document = AgentWorkspaceState.AddTurn(document, "human-1", new("turn-1", "tab-1", 1, "Style this layer"));

        document.Turns["turn-1"].Context.SelectedResources.Should().Equal(new AgentResourceReference("layer", "1"));
        AgentWorkspaceState.Current(document, "tab-1").SelectedResources.Should().Equal(new AgentResourceReference("layer", "2"));
        AgentWorkspaceState.Current(document, "tab-2").SelectedResources.Should().Equal(new AgentResourceReference("layer", "3"));
    }

    [UnitTest]
    public void RetryTurnReturnsSameBindingButDifferentInputCannotReuseId()
    {
        var request = new CreateAgentTurn("turn-1", "tab-1", 0, "Publish this layer");
        var document = AgentWorkspaceState.AddTurn(Create(), "human-1", request);
        AgentWorkspaceState.AddTurn(document, "human-1", request).Should().BeSameAs(document);
        var changed = () => AgentWorkspaceState.AddTurn(document, "human-1", request with { Prompt = "Delete it" });
        changed.Should().Throw<AgentWorkspaceConflictException>();
        var otherActor = () => AgentWorkspaceState.AddTurn(document, "agent-1", request);
        otherActor.Should().Throw<AgentWorkspaceConflictException>();
        document.Messages.Should().ContainSingle();
    }

    [UnitTest]
    public void ContextWriteRejectsStaleRevisionAndWrongTarget()
    {
        var document = AgentWorkspaceState.Update(Create(), new("tab-1", 0, "target-1", [], null, "console"));
        var stale = () => AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], null, "console"));
        stale.Should().Throw<AgentWorkspaceConflictException>();
        var switchedTarget = () => AgentWorkspaceState.Update(document, new("tab-1", 1, "target-2", [], null, "console"));
        switchedTarget.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void MembershipNeverCrossesTenantTargetOrPrincipal()
    {
        var document = Create();
        AgentWorkspaceState.CanRead(document, "agent-1", "tenant-1", "target-1").Should().BeTrue();
        AgentWorkspaceState.CanRead(document, "agent-2", "tenant-1", "target-1").Should().BeFalse();
        AgentWorkspaceState.CanRead(document, "agent-1", "tenant-2", "target-1").Should().BeFalse();
        AgentWorkspaceState.CanRead(document, "agent-1", "tenant-1", "target-2").Should().BeFalse();
    }

    [UnitTest]
    public void SecretAndPresentationFieldsAreNotSharedDraftFields()
    {
        var secret = () => AgentWorkspaceState.ValidateFields(new() { ["password"] = "not-a-shared-field" });
        secret.Should().Throw<ArgumentException>();
        var url = () => AgentWorkspaceState.ValidateFields(new() { ["browserUrl"] = "https://example.test/" });
        url.Should().Throw<ArgumentException>();
        var context = () => AgentWorkspaceState.Update(Create(), new("tab-1", 0, "target-1", [new("page", "page-1")], null, "console"));
        context.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void TaskChangesAdvanceAllViewsAndCapturedTurnKeepsItsDraft()
    {
        var document = Create();
        var fields = new Dictionary<string, string> { ["layerName"] = "roads" };
        document.Drafts["draft-1"] = new("draft-1", 0, "publish-layer", fields, []);
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], new("draft-1", 0), "console"));
        AgentWorkspaceState.Current(document, "tab-2").Revision.Should().Be(1);
        document = AgentWorkspaceState.AddTurn(document, "human-1", new("turn-1", "tab-2", 1, "Publish"));
        document.Drafts["draft-1"] = document.Drafts["draft-1"] with { Revision = 1, Fields = new() { ["layerName"] = "rivers" } };
        document = AgentWorkspaceState.Update(document, new("tab-1", 1, "target-1", [], new("draft-1", 1), "console"));
        document.Turns["turn-1"].Draft!.Fields["layerName"].Should().Be("roads");
        document.Turns["turn-1"].Context.Task!.Revision.Should().Be(0);
        var staleTask = () => AgentWorkspaceState.AddTurn(document, "human-1", new("turn-2", "tab-2", 1, "Publish again"));
        staleTask.Should().Throw<AgentWorkspaceConflictException>();
    }

    [UnitTest]
    public void MissingAndEvictedViewRevisionsCannotFallBackToCurrentSelection()
    {
        var document = Create();
        for (var revision = 0; revision < 129; revision++)
        {
            document = AgentWorkspaceState.Update(document, new("tab-1", revision, "target-1", [], null, "console"));
        }
        var evicted = () => AgentWorkspaceState.Capture(document, "tab-1", 0);
        evicted.Should().Throw<AgentWorkspaceConflictException>();
        var missing = () => AgentWorkspaceState.Capture(document, "unknown-tab", 0);
        missing.Should().Throw<AgentWorkspaceConflictException>();
    }
    [UnitTest]
    public void DelayedDraftEditKeepsNewTaskAndCapturedDraftUnchanged()
    {
        var document = Create();
        document.Drafts["draft-a"] = new("draft-a", 0, "publish-layer", new() { ["layerName"] = "roads" }, []);
        document.Drafts["draft-b"] = new("draft-b", 0, "publish-layer", new() { ["layerName"] = "rivers" }, []);
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [], new("draft-a", 0), "console"));
        document = AgentWorkspaceState.AddTurn(document, "human-1", new("turn-1", "tab-1", 1, "Rename roads"));
        document = AgentWorkspaceState.Update(document, new("tab-2", 1, "target-1", [], new("draft-b", 0), "console"));
        var before = document;
        var next = AgentWorkspaceState.PatchDraft(document, "draft-a", new(0, "tab-1", new() { ["layerName"] = "streets" }));
        next.Task.Should().Be(new AgentTaskReference("draft-b", 0));
        AgentWorkspaceState.Current(next, "tab-1").Should().Be(AgentWorkspaceState.Current(before, "tab-1"));
        AgentWorkspaceState.Current(next, "tab-2").Should().Be(AgentWorkspaceState.Current(before, "tab-2"));
        next.Drafts["draft-a"].Fields["layerName"].Should().Be("streets");
        next.Turns["turn-1"].Draft!.Fields["layerName"].Should().Be("roads");
        before.Drafts["draft-a"].Revision.Should().Be(0);
    }

    [UnitTest]
    public void EditingCurrentDraftAdvancesSharedTaskWithoutChangingSelections()
    {
        var document = Create();
        document.Drafts["draft-a"] = new("draft-a", 0, "publish-layer", new(), []);
        document = AgentWorkspaceState.Update(document, new("tab-1", 0, "target-1", [new("layer", "1")], new("draft-a", 0), "console"));
        var next = AgentWorkspaceState.PatchDraft(document, "draft-a", new(0, "tab-1", new() { ["layerName"] = "roads" }));
        next.Task.Should().Be(new AgentTaskReference("draft-a", 1));
        AgentWorkspaceState.Current(next, "tab-2").Task.Should().Be(next.Task);
        AgentWorkspaceState.Current(next, "tab-1").SelectedResources.Should().Equal(new AgentResourceReference("layer", "1"));
    }

}
