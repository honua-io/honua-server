// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Security.Claims;

namespace Honua.Ai.Protocols.Mcp.Studio;

internal static class StudioMcpAudit
{
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
