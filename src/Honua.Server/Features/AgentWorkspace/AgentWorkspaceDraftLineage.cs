// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Security;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed record AgentWorkspaceDraftLineage(string TurnId, string ActorId, string DraftId, long Revision);

internal static class AgentWorkspaceDraftLineages
{
    internal static AgentWorkspaceDraftLineage? RequireEditable(HttpContext context, AgentWorkspaceDocument workspace,
        AgentWorkspaceDraft draft, string viewId, long expectedRevision)
    {
        var turn = AgentWorkspaceCapturedContext.ReadBoundTurn(context, workspace);
        if (turn is null) return null;
        var actor = CanonicalSecurityActor.Resolve(context.User)?.ActorId ?? throw new AgentWorkspaceNotFoundException();
        if (turn.Context.ViewId != viewId || draft.Revision != expectedRevision)
            throw new AgentWorkspaceConflictException("The original turn cannot adopt a different view or later draft revision.");
        if (workspace.DraftLineages.TryGetValue(Key(turn.TurnId, draft.Id), out var lineage))
        {
            if (lineage.ActorId != actor || lineage.Revision != expectedRevision)
                throw new AgentWorkspaceConflictException("The draft changed outside this turn's recorded edits. Start a new review turn.");
            return lineage;
        }
        if (turn.Context.Task is not { Kind: "publish-layer" } task || task.Id != draft.Id || task.Revision != expectedRevision ||
            turn.Draft is not { } captured || captured.Id != draft.Id || captured.Revision != expectedRevision)
            throw new AgentWorkspaceConflictException("The draft is not the original submitted task or an output created by this turn.");
        return new(turn.TurnId, actor, draft.Id, expectedRevision);
    }

    internal static AgentWorkspaceDocument RecordCreated(HttpContext context, AgentWorkspaceDocument workspace, AgentWorkspaceDraft draft)
    {
        var turn = AgentWorkspaceCapturedContext.ReadBoundTurn(context, workspace);
        return turn is null ? workspace : Record(workspace, new(turn.TurnId,
            CanonicalSecurityActor.Resolve(context.User)?.ActorId ?? throw new AgentWorkspaceNotFoundException(), draft.Id, draft.Revision));
    }

    internal static AgentWorkspaceDocument Record(AgentWorkspaceDocument workspace, AgentWorkspaceDraftLineage? lineage)
    {
        if (lineage is null) return workspace;
        var key = Key(lineage.TurnId, lineage.DraftId);
        if (!workspace.DraftLineages.ContainsKey(key) && workspace.DraftLineages.Count >= 1024)
            throw new AgentWorkspaceConflictException("The workspace draft lineage limit was reached.");
        return workspace with { DraftLineages = new(workspace.DraftLineages, StringComparer.Ordinal) { [key] = lineage } };
    }

    private static string Key(string turnId, string draftId) => $"{turnId}:{draftId}";
}
