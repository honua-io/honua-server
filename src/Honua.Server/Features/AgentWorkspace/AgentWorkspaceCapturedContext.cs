// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.AgentWorkspace;

internal static class AgentWorkspaceCapturedContext
{
    // Header-free operator navigation intentionally edits the current view. A bound
    // agent turn can propose future context only from its original captured view/revision.
    internal static void RequireOriginal(HttpContext context, AgentWorkspaceDocument workspace, string viewId, long revision)
    {
        var workspaceId = context.Request.Headers["X-Honua-Workspace"].ToString();
        var turnId = context.Request.Headers["X-Honua-Turn"].ToString();
        if (workspaceId.Length == 0 && turnId.Length == 0) return;
        if (workspaceId != workspace.WorkspaceId || !AgentWorkspaceState.IsIdentifier(turnId) ||
            !workspace.Turns.TryGetValue(turnId, out var turn) || turn.Context.WorkspaceId != workspace.WorkspaceId ||
            turn.Context.TargetId != workspace.TargetId || turn.Context.TenantId != workspace.TenantId ||
            turn.Context.ViewId != viewId || turn.Context.Revision != revision)
            throw new AgentWorkspaceConflictException("The request does not match the original submitted turn context. Start a new turn for the current selection.");
    }
}
