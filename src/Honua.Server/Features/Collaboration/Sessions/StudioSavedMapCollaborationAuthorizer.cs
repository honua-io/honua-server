// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Collaboration.Sessions;

/// <summary>
/// Studio-lifecycle-backed saved-map collaboration authorizer (honua-server#2999, REQ-001/AC-3).
/// Resolves the collaboration <c>mapId</c> to a Studio package draft and delegates the decision
/// to the same <see cref="IStudioAuthorizationService"/> policy layer the Studio lifecycle
/// endpoints use — admins pass unconditionally, and with end-user authorization enabled the
/// resource owner passes — with denials audited through the shared
/// <see cref="StudioEndpointAuthorization"/> seam. A <c>mapId</c> that does not resolve to a
/// Studio package draft (including content-item ids, which the checkpoint surface cannot yet
/// normalize to a draft) is denied fail-closed, preserving the pre-#2999 posture for unknown
/// map identifiers.
/// </summary>
/// <remarks>
/// Registered as a singleton (the session service that consumes it is a singleton) while the
/// Studio lifecycle/authorization services are scoped, so the scoped services are resolved from
/// the ambient request's <see cref="HttpContext.RequestServices"/> per call. Collaboration joins
/// only ever originate from HTTP requests (REST join, op-log append/replay, WebSocket upgrade,
/// checkpoint); a call without an ambient request is denied.
/// </remarks>
internal sealed class StudioSavedMapCollaborationAuthorizer : ISavedMapCollaborationAuthorizer
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public StudioSavedMapCollaborationAuthorizer(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor ?? throw new ArgumentNullException(nameof(httpContextAccessor));
    }

    public async ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeJoinAsync(
        string mapId,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        ArgumentNullException.ThrowIfNull(principal);

        if (principal.Identity?.IsAuthenticated != true)
        {
            return SavedMapCollaborationAuthorizationResult.RequireAuthentication(
                "Authentication is required to join a saved-map collaboration session.");
        }

        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            return SavedMapCollaborationAuthorizationResult.Forbid(
                "Saved-map collaboration can only be authorized within an HTTP request.");
        }

        if (!Guid.TryParse(mapId, out var resolvedId))
        {
            return SavedMapCollaborationAuthorizationResult.Forbid(
                "The map id does not resolve to a Studio content item or draft.");
        }

        var lifecycle = context.RequestServices.GetRequiredService<IStudioPackageLifecycleService>();
        var authorization = context.RequestServices.GetRequiredService<StudioEndpointAuthorization>();

        // Live co-editing sessions target exactly the mutable draft that checkpoints save as
        // immutable versions, so only draft ids are accepted. Content-item ids are rejected
        // fail-closed: the lifecycle surface has no item -> active-draft resolution yet, so an
        // item-scoped session could accept live edits that the checkpoint endpoint (which
        // resolves the id as a draft) could never persist. Accept item ids only once the
        // session, op-log, and checkpoint surfaces can all normalize them to the same canonical
        // draft id.
        var draft = await lifecycle.GetDraftAsync(resolvedId, cancellationToken).ConfigureAwait(false);
        if (draft is not null)
        {
            var decision = await authorization.AuthorizeAsync(
                context,
                StudioAuthorizationOperation.UpdateDraft,
                draft.OwnerId,
                resourceType: "studio-package-draft",
                resourceId: draft.DraftId.ToString("D")).ConfigureAwait(false);
            return ToResult(decision);
        }

        return SavedMapCollaborationAuthorizationResult.Forbid(
            "The map id does not resolve to a Studio package draft.");
    }

    private static SavedMapCollaborationAuthorizationResult ToResult(StudioAuthorizationDecision decision) =>
        decision.IsAllowed
            ? SavedMapCollaborationAuthorizationResult.Allow()
            : SavedMapCollaborationAuthorizationResult.Forbid(
                decision.Reason ?? "You are not allowed to collaborate on this saved map.");
}
