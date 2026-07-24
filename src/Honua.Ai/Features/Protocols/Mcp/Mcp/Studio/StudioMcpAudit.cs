// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;

namespace Honua.Ai.Protocols.Mcp.Studio;

/// <summary>
/// Shared per-call audit recorder for the Studio draft lifecycle and
/// composition-mutation MCP tools (honua-server#3002, NFR-001: "every tool
/// call is audited with session identity and draft generation before/after").
/// Uses the same established audit mechanism as the rest of the MCP surface —
/// structured logging (<see cref="McpLog"/>) plus <see cref="Activity"/> tag
/// enrichment (mirrors <see cref="McpTelemetry.EnrichActivity"/> and the
/// <c>studio.*</c> tags <c>StudioPackageLifecycleService</c> already emits on
/// its own activities) — rather than introducing a parallel audit store.
/// </summary>
internal static class StudioMcpAudit
{
    /// <summary>
    /// Records one Studio tool call. <paramref name="draftId"/> is omitted (null)
    /// for a call that never resolved a draft id (e.g. a create-draft call whose
    /// argument parsing failed before a draft id existed).
    /// </summary>
    public static void Record(
        ILogger logger,
        ClaimsPrincipal principal,
        string toolName,
        Guid? draftId,
        long? generationBefore,
        long? generationAfter)
    {
        var principalKey = McpAuthorizationHelper.ResolvePrincipalKey(principal);
        var draftIdText = draftId?.ToString("D") ?? "(none)";

        McpLog.StudioDraftAudited(logger, toolName, principalKey, draftIdText, generationBefore, generationAfter);

        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        activity.SetTag("studio.mcp.principal", principalKey);
        if (draftId is { } id)
        {
            activity.SetTag("studio.draft.id", id.ToString("D"));
        }

        if (generationBefore is { } before)
        {
            activity.SetTag("studio.generation.before", before);
        }

        if (generationAfter is { } after)
        {
            activity.SetTag("studio.generation.after", after);
        }
    }
}
