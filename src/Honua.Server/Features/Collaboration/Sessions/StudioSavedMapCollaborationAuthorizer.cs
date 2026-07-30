// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
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
            // Only composition-eligible families (Map/App) can be checkpointed: the checkpoint
            // applies operations onto the Studio composition body via
            // StudioCompositionBodyEditor, which rejects every other family. Authorizing a
            // query/analysis/dashboard/report/form draft would hand clients accepted cursors and
            // live broadcasts for edits no checkpoint could ever persist, so refuse before any
            // operation enters the log (honua-server#2999 review).
            if (!StudioCompositionBodyEditor.CompositionEligibleFamilies.Contains(draft.Family))
            {
                return SavedMapCollaborationAuthorizationResult.Forbid(
                    $"Saved-map collaboration is not available for '{draft.Family}' drafts; only " +
                    "map and app drafts can be checkpointed.");
            }

            var decision = await authorization.AuthorizeAsync(
                context,
                StudioAuthorizationOperation.UpdateDraft,
                draft.OwnerId,
                resourceType: "studio-package-draft",
                resourceId: draft.DraftId.ToString("D")).ConfigureAwait(false);
            if (!decision.IsAllowed)
            {
                return ToResult(decision);
            }

            // Same reasoning as the composition-family guard above, one axis over: admitting a
            // caller who can never checkpoint hands out accepted cursors and live broadcasts for
            // edits that can never reach an immutable version. Checkpointing advances the parent
            // content item's current pointer, so it authorizes CreateVersion against the ITEM
            // owner as well as the draft owner — and for a mixed-owner draft (draft owner is not
            // the item owner) the draft owner is intentionally denied at that boundary. UpdateDraft
            // on the draft alone therefore over-admits: the caller edits and broadcasts, then
            // every checkpoint 403s and the operations accumulate until they age out of the
            // retained window and the continuity guard refuses too (honua-server#2999 review).
            //
            // Enforced with the SAME operation, resource type, and owner the checkpoint endpoint
            // uses, so the two boundaries cannot drift apart.
            var pointers = await lifecycle.GetPointersAsync(draft.ItemId, cancellationToken).ConfigureAwait(false);
            if (pointers is null)
            {
                return SavedMapCollaborationAuthorizationResult.Forbid(
                    "The saved map's parent Studio content item was not found.");
            }

            var itemDecision = await authorization.AuthorizeAsync(
                context,
                StudioAuthorizationOperation.CreateVersion,
                pointers.OwnerId,
                resourceType: "studio-content-item",
                resourceId: draft.ItemId.ToString("D")).ConfigureAwait(false);
            if (!itemDecision.IsAllowed)
            {
                return SavedMapCollaborationAuthorizationResult.Forbid(
                    itemDecision.Reason
                    ?? "You are not allowed to collaborate on this saved map: its edits could "
                    + "never be checkpointed onto the parent content item.");
            }

            return SavedMapCollaborationAuthorizationResult.Allow();
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
