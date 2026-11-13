// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Security;
using Honua.Server.Features.Studio;

namespace Honua.Server.Features.AgentWorkspace;

internal sealed class AgentWorkspaceAuthorization(AgentWorkspaceIdentityStore store, IConfiguration configuration)
{
    public async Task<LocalProcessWorkspaceAccess> AuthorizeAsync(HttpContext context, string workspaceId)
    {
        var current = ResolveCaller(context);
        if (!Guid.TryParseExact(workspaceId, "N", out _))
            throw new LocalProcessWorkspaceException(404, "Workspace not found.");
        var identity = await store.GetAsync(workspaceId, context.RequestAborted).ConfigureAwait(false);
        if (identity is null || identity.OwnerActorId != current.ActorId || identity.TenantId != current.TenantId || identity.TargetId != current.TargetId)
            throw new LocalProcessWorkspaceException(404, "Workspace not found.");
        return current with { WorkspaceId = identity.WorkspaceId, OwnerActorId = identity.OwnerActorId };
    }

    public LocalProcessWorkspaceAccess ResolveCaller(HttpContext context)
    {
        var actor = CanonicalSecurityActor.Resolve(context.User);
        if (actor is not { IsDurablyRevalidatable: true } || !StudioAiInteractivePrincipal.IsInteractive(context.User))
            throw new LocalProcessWorkspaceException(403, "An authenticated human with a stable identity is required.");
        var targetId = string.IsNullOrWhiteSpace(configuration[StudioSessionEndpoints.EnvironmentIdKey])
            ? null : StudioSessionEndpoints.ResolveEnvironmentId(configuration);
        if (targetId is null)
            throw new LocalProcessWorkspaceException(503, "An explicit Studio environment identity must be configured.");
        return new LocalProcessWorkspaceAccess(actor.ActorId, actor.ActorId, string.Empty, targetId,
            StudioEndpointAuthorization.ResolveRequestTenantId(context), false);
    }
}
