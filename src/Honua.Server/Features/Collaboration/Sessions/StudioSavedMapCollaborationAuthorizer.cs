// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Domain;
using Honua.Core.Features.Studio.Services;
using Honua.Infrastructure.Security;

namespace Honua.Server.Features.Collaboration.Sessions;

// An unresolved map id is denied; a join reads scoped Studio services from the ambient request (none: denied), a re-check from the request it is given.
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

        return await AuthorizeInContextAsync(context, mapId, cancellationToken).ConfigureAwait(false);
    }

    // An open session is checked again with the request it streams on, so Studio services and grants are read fresh.
    public async ValueTask<SavedMapCollaborationAuthorizationResult> ReauthorizeAsync(HttpContext context, string mapId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(mapId);
        return context.User.Identity?.IsAuthenticated == true
            ? await AuthorizeInContextAsync(context, mapId, context.RequestAborted).ConfigureAwait(false)
            : SavedMapCollaborationAuthorizationResult.RequireAuthentication(
                "Authentication is required to stay in a saved-map collaboration session.");
    }

    private static async ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeInContextAsync(
        HttpContext context, string mapId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(mapId, out var resolvedId))
        {
            return SavedMapCollaborationAuthorizationResult.Forbid(
                "The map id does not resolve to a Studio content item or draft.");
        }

        var lifecycle = context.RequestServices.GetRequiredService<IStudioPackageLifecycleService>();

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
            return await AuthorizeDraftAsync(context, draft, cancellationToken).ConfigureAwait(false);
        }

        return SavedMapCollaborationAuthorizationResult.Forbid(
            "The map id does not resolve to a Studio package draft.");
    }

    private static async ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeDraftAsync(
        HttpContext context, StudioPackageDraft draft, CancellationToken cancellationToken)
    {
        var authorization = context.RequestServices.GetRequiredService<StudioEndpointAuthorization>();

        // Only composition-eligible families (Map/App) can be checkpointed: the checkpoint
        // applies operations onto the Studio composition body via
        // StudioCompositionBodyEditor, which rejects every other family. Authorizing a
        // query/analysis/dashboard/report/form draft would hand clients accepted cursors and
        // live broadcasts for edits no checkpoint could ever persist, so refuse before any
        // operation enters the log.
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
            draft.TenantId,
            resourceType: "studio-package-draft",
            resourceId: draft.DraftId.ToString("D")).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return ToResult(decision);
        }

        return await AuthorizeCheckpointTargetAsync(context, draft, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<SavedMapCollaborationAuthorizationResult> AuthorizeCheckpointTargetAsync(
        HttpContext context, StudioPackageDraft draft, CancellationToken cancellationToken)
    {
        var lifecycle = context.RequestServices.GetRequiredService<IStudioPackageLifecycleService>();
        var authorization = context.RequestServices.GetRequiredService<StudioEndpointAuthorization>();

        // As with the composition-family guard in AuthorizeDraftAsync: a caller who can never checkpoint
        // would edit and broadcast while every checkpoint 403s. Checkpointing authorizes CreateVersion
        // against the ITEM owner as well (a mixed-owner draft owner is denied there), so the same
        // operation, resource type and owner are enforced here and the two boundaries cannot drift apart.
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
            pointers.TenantId,
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

    private static SavedMapCollaborationAuthorizationResult ToResult(StudioAuthorizationDecision decision) =>
        decision.IsAllowed
            ? SavedMapCollaborationAuthorizationResult.Allow()
            : SavedMapCollaborationAuthorizationResult.Forbid(
                decision.Reason ?? "You are not allowed to collaborate on this saved map.");
}
