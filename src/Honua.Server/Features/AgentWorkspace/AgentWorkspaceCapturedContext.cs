// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentWorkspaceCapturedContext
{
    internal static AgentWorkspaceTurn? ReadBoundTurn(HttpContext context, AgentWorkspaceDocument workspace)
    {
        var workspaceId = context.Request.Headers["X-Honua-Workspace"].ToString();
        var turnId = context.Request.Headers["X-Honua-Turn"].ToString();
        if (workspaceId.Length == 0 && turnId.Length == 0) return null;
        if (workspaceId != workspace.WorkspaceId || !AgentWorkspaceState.IsIdentifier(turnId) ||
            !workspace.Turns.TryGetValue(turnId, out var turn) || turn.Context.WorkspaceId != workspace.WorkspaceId ||
            turn.Context.TargetId != workspace.TargetId || turn.Context.TenantId != workspace.TenantId)
            throw new AgentWorkspaceConflictException("The request does not match the original submitted turn scope.");
        return turn;
    }

    // Header-free operator navigation intentionally edits the current view. A bound
    // agent turn can propose future context only from its original captured view/revision.
    internal static void RequireOriginal(HttpContext context, AgentWorkspaceDocument workspace, string viewId, long revision)
    {
        var turn = ReadBoundTurn(context, workspace);
        if (turn is not null && (turn.Context.ViewId != viewId || turn.Context.Revision != revision))
            throw new AgentWorkspaceConflictException("The request does not match the original submitted turn context. Start a new turn for the current selection.");
    }
}
